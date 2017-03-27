// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Internal;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal class AuthenticationHandler : IAuthenticationHandler
    {
        private RequestContext _requestContext;
        private AuthenticationSchemes _authSchemes;
        private AuthenticationSchemes _customChallenges;

        public Task<AuthenticateResult> AuthenticateAsync(AuthenticateContext context)
        {
            var identity = (ClaimsIdentity)_requestContext.User?.Identity;
            var success = identity != null 
                && identity.IsAuthenticated 
                && string.Equals(context.AuthenticationScheme, identity.AuthenticationType, StringComparison.Ordinal);
            return Task.FromResult(success
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties: null, authenticationScheme: context.AuthenticationScheme))
                : AuthenticateResult.None());
        }

        public Task ChallengeAsync(ChallengeContext context)
        {
            if (!Enum.TryParse<AuthenticationSchemes>(context.AuthenticationScheme, out var scheme))
            {
                throw new NotSupportedException(context.AuthenticationScheme);
            }

            switch (context.Behavior)
            {
                case ChallengeBehavior.Forbidden:
                    _requestContext.Response.StatusCode = 403;
                    break;
                case ChallengeBehavior.Unauthorized:
                    _requestContext.Response.StatusCode = 401;
                    _customChallenges |= scheme;
                    break;
                case ChallengeBehavior.Automatic:
                    var identity = (ClaimsIdentity)_requestContext.User?.Identity;
                    if (identity != null && identity.IsAuthenticated
                        && (string.Equals(identity.AuthenticationType, context.AuthenticationScheme, StringComparison.Ordinal)))
                    {
                        _requestContext.Response.StatusCode = 403;
                    }
                    else
                    {
                        _requestContext.Response.StatusCode = 401;
                        _customChallenges |= scheme;
                    }
                    break;
                default:
                    throw new NotSupportedException(context.Behavior.ToString());
            }

            // A challenge was issued, it overrides any pre-set auth types.
            _requestContext.Response.AuthenticationChallenges = _customChallenges;
            return TaskCache.CompletedTask;
        }

        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
        {
            _requestContext = context.Features.Get<RequestContext>();

            if (_requestContext == null)
            {
                throw new InvalidOperationException("No RequestContext found.");
            }

            _authSchemes = _requestContext.Response.AuthenticationChallenges;
            _customChallenges = AuthenticationSchemes.None;
            return TaskCache.CompletedTask;
        }

        public Task SignInAsync(SignInContext context)
        {
            throw new NotSupportedException();
        }

        public Task SignOutAsync(SignOutContext context)
        {
            return TaskCache.CompletedTask;
        }
    }
}