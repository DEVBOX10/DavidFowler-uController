﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Web.Framework
{
    public partial class HttpHandler
    {
        private static readonly MethodInfo ChangeTypeMethodInfo = GetMethodInfo<Func<object, Type, object>>((value, type) => Convert.ChangeType(value, type));
        private static readonly MethodInfo ExecuteTaskOfTMethodInfo = typeof(HttpHandler).GetMethod(nameof(ExecuteTask), BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo ExecuteAsyncMethodInfo = typeof(HttpHandler).GetMethod(nameof(ExecuteResultAsync), BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo ActivatorMethodInfo = typeof(HttpHandler).GetMethod(nameof(CreateInstance), BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo GetRequiredServiceMethodInfo = typeof(HttpHandler).GetMethod(nameof(GetRequiredService), BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo FormatterRead = typeof(IHttpRequestFormatter).GetMethod(nameof(IHttpRequestFormatter.Read), BindingFlags.Public | BindingFlags.Instance);

        private static readonly MemberInfo CompletedTaskMemberInfo = GetMemberInfo<Func<Task>>(() => Task.CompletedTask);

        public static List<Endpoint> Build<THttpHandler>()
        {
            return Build(typeof(THttpHandler));
        }

        public static List<Endpoint> Build(Type handlerType)
        {
            var model = HttpModel.FromType(handlerType);

            var endpoints = new List<Endpoint>();

            foreach (var method in model.Methods)
            {
                // Nothing to route to
                if (method.RoutePattern == null)
                {
                    continue;
                }

                var needForm = false;
                var needBody = false;
                // Non void return type

                // Task Invoke(HttpContext httpContext, RouteValueDictionary routeValues, RequestDelegate next)
                // {
                //     // The type is activated via DI if it has args
                //     return ExecuteResult(new THttpHandler(...).Method(..), httpContext);
                // }

                // void return type

                // Task Invoke(HttpContext httpContext, RouteValueDictionary routeValues, RequestDelegate next)
                // {
                //     new THttpHandler(...).Method(..)
                //     return Task.CompletedTask;
                // }

                var httpContextArg = Expression.Parameter(typeof(HttpContext), "httpContext");
                var requestServicesExpr = Expression.Property(httpContextArg, nameof(HttpContext.RequestServices));

                // Fast path: We can skip the activator if there's only a default ctor with 0 args
                var ctors = handlerType.GetConstructors();

                Expression httpHandlerExpression = null;

                if (method.MethodInfo.IsStatic)
                {
                    // Do nothing
                }
                else if (ctors.Length == 1 && ctors[0].GetParameters().Length == 0)
                {
                    httpHandlerExpression = Expression.New(ctors[0]);
                }
                else
                {
                    // CreateInstance<THttpHandler>(context.RequestServices)
                    httpHandlerExpression = Expression.Call(ActivatorMethodInfo.MakeGenericMethod(handlerType), requestServicesExpr);
                }

                var args = new List<Expression>();

                var httpRequestExpr = Expression.Property(httpContextArg, nameof(HttpContext.Request));

                foreach (var parameter in method.Parameters)
                {
                    Expression paramterExpression = Expression.Default(parameter.ParameterType);

                    if (parameter.FromQuery != null)
                    {
                        var queryProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Query));
                        paramterExpression = BindArgument(queryProperty, parameter, parameter.FromQuery);
                    }
                    else if (parameter.FromHeader != null)
                    {
                        var headersProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Headers));
                        paramterExpression = BindArgument(headersProperty, parameter, parameter.FromHeader);
                    }
                    else if (parameter.FromRoute != null)
                    {
                        var routeValuesProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.RouteValues));
                        paramterExpression = BindArgument(routeValuesProperty, parameter, parameter.FromRoute);
                    }
                    else if (parameter.FromCookie != null)
                    {
                        var cookiesProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Cookies));
                        paramterExpression = BindArgument(cookiesProperty, parameter, parameter.FromCookie);
                    }
                    else if (parameter.FromServices)
                    {
                        paramterExpression = Expression.Call(GetRequiredServiceMethodInfo.MakeGenericMethod(parameter.ParameterType), requestServicesExpr);
                    }
                    else if (parameter.FromForm != null)
                    {
                        needForm = true;

                        var formProperty = Expression.Property(httpRequestExpr, nameof(HttpRequest.Form));
                        paramterExpression = BindArgument(formProperty, parameter, parameter.FromForm);
                    }
                    else if (parameter.FromBody)
                    {
                        needBody = true;

                        paramterExpression = BindBody(httpContextArg, requestServicesExpr, parameter);
                    }
                    else
                    {
                        if (parameter.ParameterType == typeof(IFormCollection))
                        {
                            paramterExpression = Expression.Property(httpRequestExpr, nameof(HttpRequest.Form));
                        }
                        else if (parameter.ParameterType == typeof(HttpContext))
                        {
                            paramterExpression = httpContextArg;
                        }
                        else if (parameter.ParameterType == typeof(IHeaderDictionary))
                        {
                            paramterExpression = Expression.Property(httpRequestExpr, nameof(HttpRequest.Headers));
                        }
                    }

                    args.Add(paramterExpression);
                }

                Expression body = null;

                if (method.MethodInfo.ReturnType == typeof(void))
                {
                    var bodyExpressions = new List<Expression>
                    {
                        Expression.Call(httpHandlerExpression, method.MethodInfo, args),
                        Expression.Property(null, (PropertyInfo)CompletedTaskMemberInfo)
                    };

                    body = Expression.Block(bodyExpressions);
                }
                else
                {
                    var methodCall = Expression.Call(httpHandlerExpression, method.MethodInfo, args);

                    // Coerce Task<T> to Task<object>
                    if (method.MethodInfo.ReturnType.IsGenericType &&
                        method.MethodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        var typeArg = method.MethodInfo.ReturnType.GetGenericArguments()[0];

                        // ExecuteTask<T>(handler.Method(..), httpContext);
                        body = Expression.Call(
                                           ExecuteTaskOfTMethodInfo.MakeGenericMethod(typeArg),
                                           methodCall,
                                           httpContextArg);
                    }
                    else
                    {
                        // ExecuteResult(handler.Method(..), httpContext);
                        body = Expression.Call(ExecuteAsyncMethodInfo, methodCall, httpContextArg);
                    }
                }

                var lambda = Expression.Lambda<RequestDelegate>(body, httpContextArg);

                var routeTemplate = method.RoutePattern;

                var invoker = lambda.Compile();

                var routeEndpointModel = new RouteEndpointBuilder(
                    httpContext =>
                    {
                        async Task ExecuteAsyncAwaited()
                        {
                            // Generating async code would just be insane so if the method needs the form populate it here
                            // so the within the method it's cached
                            if (needForm)
                            {
                                await httpContext.Request.ReadFormAsync();
                            }
                            else if (needBody)
                            {
                                var request = httpContext.Request;
                                if (!request.Body.CanSeek)
                                {
                                    // JSON.Net does synchronous reads. In order to avoid blocking on the stream, we asynchronously
                                    // read everything into a buffer, and then seek back to the beginning.
                                    request.EnableBuffering();
                                    Debug.Assert(request.Body.CanSeek);

                                    await request.Body.DrainAsync(CancellationToken.None);
                                    request.Body.Seek(0L, SeekOrigin.Begin);
                                }
                            }

                            await invoker(httpContext);
                        }

                        if (needForm || needBody)
                        {
                            return ExecuteAsyncAwaited();
                        }

                        return invoker(httpContext);
                    },
                    routeTemplate,
                    0)
                {
                    DisplayName = method.MethodInfo.DeclaringType.Name + "." + method.MethodInfo.Name
                };

                foreach (var metadata in method.Metadata)
                {
                    routeEndpointModel.Metadata.Add(metadata);
                }

                endpoints.Add(routeEndpointModel.Build());
            }

            return endpoints;
        }

        private static Expression BindBody(Expression httpContext, Expression requestServicesExpr, ParameterModel parameter)
        {
            // TODO: This *really* needs to generate async code but that's too hard
            // var formatter = httpContext.GetRequiredService<IHttpRequestFormatter>();
            // formatter.Read(httpContext, parameter.ParameterType);

            var formatterExpr = Expression.Call(GetRequiredServiceMethodInfo.MakeGenericMethod(typeof(IHttpRequestFormatter)), requestServicesExpr);
            var readExpr = Expression.Call(formatterExpr, FormatterRead.MakeGenericMethod(parameter.ParameterType), httpContext);

            return readExpr;
        }

        private static Expression BindArgument(Expression sourceExpression, ParameterModel parameter, string name)
        {
            var key = name ?? parameter.Name;
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            var valueArg = Expression.Convert(
                                Expression.MakeIndex(sourceExpression,
                                                     sourceExpression.Type.GetProperty("Item"),
                                                     new[] {
                                                         Expression.Constant(key)
                                                     }),
                                typeof(string));

            MethodInfo parseMethod = (from m in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                      let parameters = m.GetParameters()
                                      where m.Name == "Parse" && parameters.Length == 1 && parameters[0].ParameterType == typeof(string)
                                      select m).FirstOrDefault();

            Expression expr = null;

            if (parseMethod != null)
            {
                expr = Expression.Call(parseMethod, valueArg);
            }
            else if (parameter.ParameterType != valueArg.Type)
            {
                // Convert.ChangeType()
                expr = Expression.Call(ChangeTypeMethodInfo, valueArg, Expression.Constant(type));
            }
            else
            {
                expr = valueArg;
            }

            if (expr.Type != parameter.ParameterType)
            {
                expr = Expression.Convert(expr, parameter.ParameterType);
            }

            // property[key] == null ? default : (ParameterType){Type}.Parse(property[key]);
            expr = Expression.Condition(
                Expression.Equal(valueArg, Expression.Constant(null)),
                Expression.Default(parameter.ParameterType),
                expr);

            return expr;
        }

        private static MethodInfo GetMethodInfo<T>(Expression<T> expr)
        {
            var mc = (MethodCallExpression)expr.Body;
            return mc.Method;
        }

        private static MemberInfo GetMemberInfo<T>(Expression<T> expr)
        {
            var mc = (MemberExpression)expr.Body;
            return mc.Member;
        }

        private static T GetRequiredService<T>(IServiceProvider sp)
        {
            return sp.GetRequiredService<T>();
        }

        private static T CreateInstance<T>(IServiceProvider sp)
        {
            return ActivatorUtilities.CreateInstance<T>(sp);
        }

        private static async Task ExecuteTask<T>(Task<T> task, HttpContext httpContext)
        {
            var result = await task;
            await ExecuteResultAsync(result, httpContext);
        }

        private static async Task ExecuteResultAsync(object result, HttpContext httpContext)
        {
            switch (result)
            {
                case Task task:
                    await task;
                    break;
                case RequestDelegate val:
                    await val(httpContext);
                    break;
                case null:
                    httpContext.Response.StatusCode = 404;
                    break;
                default:
                    {
                        var val = new ObjectResult(result);
                        await val.ExecuteAsync(httpContext);
                    }
                    break;
            }
        }
    }
}
