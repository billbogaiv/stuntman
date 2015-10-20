﻿using Microsoft.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OAuth;
using Owin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace RimDev.Stuntman.Core
{
    public static class IAppBuilderExtensions
    {
        public const string StuntmanAuthenticationType = "StuntmanAuthentication";

        public static void UseStuntman(this IAppBuilder app, StuntmanOptions options)
        {
            options.VerifyUsageIsPermitted();

            app.UseOAuthBearerAuthentication(new OAuthBearerAuthenticationOptions()
            {
                AuthenticationType = StuntmanAuthenticationType,
                Provider = new StuntmanOAuthBearerProvider(options),
                AccessTokenFormat = new StuntmanOAuthAccessTokenFormat()
            });

            app.UseCookieAuthentication(new CookieAuthenticationOptions
            {
                AuthenticationType = StuntmanAuthenticationType,
                LoginPath = new PathString(options.SignInUri),
                LogoutPath = new PathString(options.SignOutUri),
                ReturnUrlParameter = StuntmanOptions.ReturnUrlQueryStringKey,
            });

            app.Map(options.SignInUri, signin =>
            {
                signin.Use(async (context, next) =>
                {
                    var claims = new List<Claim>();

                    var overrideUserId = context.Request.Query[StuntmanOptions.OverrideQueryStringKey];

                    if (string.IsNullOrWhiteSpace(overrideUserId))
                    {
                        await next.Invoke();

                        ShowLoginUI(context, options);
                    }
                    else
                    {
                        var user = options.Users
                            .Where(x => x.Id == overrideUserId)
                            .FirstOrDefault();

                        if (user == null)
                        {
                            context.Response.StatusCode = 404;
                            await context.Response.WriteAsync(
                                $"options provided does not include the requested '{overrideUserId}' user.");

                            return;
                        }

                        claims.Add(new Claim(ClaimTypes.Name, user.Name));
                        claims.AddRange(user.Claims);

                        var identity = new ClaimsIdentity(claims, StuntmanAuthenticationType);

                        var authManager = context.Authentication;

                        authManager.SignIn(identity);

                        await next.Invoke();
                    }
                });

                RedirectToReturnUrl(signin);
            });

            app.Map(options.SignOutUri, signout =>
            {
                signout.Use((context, next) =>
                {
                    var authManager = context.Authentication;
                    authManager.SignOut(StuntmanAuthenticationType);

                    return next.Invoke();
                });

                RedirectToReturnUrl(signout);
            });
        }

        private static string GetUsersLoginUI(
            IOwinContext context,
            StuntmanOptions options)
        {
            var usersHtml = new StringBuilder();

            foreach (var user in options.Users)
            {
                var href = $"{options.SignInUri}?OverrideUserId={user.Id}&{StuntmanOptions.ReturnUrlQueryStringKey}={WebUtility.UrlEncode(context.Request.Query[StuntmanOptions.ReturnUrlQueryStringKey])}";

                usersHtml.Append($@"<li><a href=""{href}"">{user.Name}</a></li>");
            }

            return usersHtml.ToString();
        }

        private static void RedirectToReturnUrl(IAppBuilder app)
        {
            app.Run(context =>
            {
                context.Response.Headers.Add("Location", new[]
                {
                    context.Request.Query[StuntmanOptions.ReturnUrlQueryStringKey]
                });

                context.Response.StatusCode = 302;

                return Task.FromResult(true);
            });
        }

        private static void ShowLoginUI(
            IOwinContext context,
            StuntmanOptions options)
        {
            context.Response.ContentType = "text/html";
            context.Response.StatusCode = 200;

            var css = Resources.GetCss();
            var usersHtml = GetUsersLoginUI(context, options);

            context.Response.Write($@"
<!DOCTYPE html>
<html>
    <head>
        <meta charset=""UTF-8"">
        <title>Select a user</title>
        <style>
            {css}
        </style>
    </head>
    <body>
        <div class=""stuntman-login-ui-container"">
            <img src=""https://raw.githubusercontent.com/ritterim/stuntman/gh-pages/images/stuntman-logo.png"" />
            <h3>Please select a user to continue authentication.</h3>
            <ul>
                {usersHtml}
            </ul>
        </div>
    </body>
</html>");
        }

        private class StuntmanOAuthBearerProvider : OAuthBearerAuthenticationProvider
        {
            public StuntmanOAuthBearerProvider(StuntmanOptions options)
            {
                this.options = options;
            }

            private readonly StuntmanOptions options;

            public override Task ValidateIdentity(OAuthValidateIdentityContext context)
            {
                var authorizationBearerToken = context.Request.Headers["Authorization"];

                if (string.IsNullOrWhiteSpace(authorizationBearerToken))
                {
                    context.Rejected();

                    return Task.FromResult(false);
                }
                else
                {
                    var authorizationBearerTokenParts = authorizationBearerToken
                        .Split(' ');

                    var accessToken = authorizationBearerTokenParts
                        .LastOrDefault();

                    var claims = new List<Claim>();
                    StuntmanUser user = null;

                    if (authorizationBearerTokenParts.Count() != 2 ||
                        string.IsNullOrWhiteSpace(accessToken))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.ReasonPhrase = "Authorization header is not in correct format.";

                        context.Rejected();

                        return Task.FromResult(false);
                    }
                    else
                    {
                        user = options.Users
                            .Where(x => x.AccessToken == accessToken)
                            .FirstOrDefault();

                        if (user == null)
                        {
                            context.Response.StatusCode = 403;
                            context.Response.ReasonPhrase =
                                $"options provided does not include the requested '{accessToken}' user.";

                            context.Rejected();

                            return Task.FromResult(false);
                        }
                        else
                        {
                            claims.Add(new Claim("access_token", accessToken));
                        }
                    }

                    claims.Add(new Claim(ClaimTypes.Name, user.Name));
                    claims.AddRange(user.Claims);

                    var identity = new ClaimsIdentity(claims, StuntmanAuthenticationType);

                    context.Validated(identity);

                    var authManager = context.OwinContext.Authentication;

                    authManager.SignIn(identity);

                    if (options.AfterBearerValidateIdentity != null)
                    {
                        options.AfterBearerValidateIdentity(context);
                    }

                    return Task.FromResult(true);
                }
            }
        }

        private class StuntmanOAuthAccessTokenFormat : ISecureDataFormat<AuthenticationTicket>
        {
            public string Protect(AuthenticationTicket data)
            {
                throw new NotSupportedException(
                    "Stuntman does not protect data.");
            }

            public AuthenticationTicket Unprotect(string protectedText)
            {
                return new AuthenticationTicket(
                    identity: new ClaimsIdentity(),
                    properties: new AuthenticationProperties());
            }
        }
    }
}
