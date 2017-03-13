// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
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
            var identity = _requestContext.User?.Identity;

            foreach (var authType in ListEnabledAuthSchemes())
            {
                var authScheme = authType.ToString();
                if (string.Equals(authScheme, context.AuthenticationScheme, StringComparison.Ordinal))
                {
                    if (identity != null && identity.IsAuthenticated
                        && string.Equals(authScheme, identity.AuthenticationType, StringComparison.Ordinal))
                    {
                        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties: null, authenticationScheme: context.AuthenticationScheme)));
                    }
                }
            }
            return Task.FromResult(AuthenticateResult.None());
        }

        public Task ChallengeAsync(ChallengeContext context)
        {
            //var automaticChallenge = string.Equals("Automatic", context.AuthenticationScheme, StringComparison.Ordinal);
            foreach (var scheme in ListEnabledAuthSchemes())
            {
                var authScheme = scheme.ToString();
                // Not including any auth types means it's a blanket challenge for any auth type.
                if (string.Equals(context.AuthenticationScheme, authScheme, StringComparison.Ordinal))
                {
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
                }
            }
            // A challenge was issued, it overrides any pre-set auth types.
            _requestContext.Response.AuthenticationChallenges = _customChallenges;
            return TaskCache.CompletedTask;
        }

        public Task<bool> HandleRequestAsync()
        {
            throw new NotImplementedException();
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
            // Not supported. AuthenticationManager will throw if !Accepted.
            return TaskCache.CompletedTask;
        }

        public Task SignOutAsync(SignOutContext context)
        {
            // Not supported. AuthenticationManager will throw if !Accepted.
            return TaskCache.CompletedTask;
        }

<<<<<<< HEAD
        private IDictionary<string, object> GetDescription(string authenticationScheme)
        {
            return new Dictionary<string, object>()
            {
                { "AuthenticationScheme", authenticationScheme },
            };
        }
=======
        //private IDictionary<string, object> GetDescription(string authenticationScheme)
        //{
        //    return new Dictionary<string, object>()
        //    {
        //        { "AuthenticationScheme", authenticationScheme },
        //        { "DisplayName", "Windows:" + authenticationScheme },
        //    };
        //}
>>>>>>> Prototype Auth 2.0 changes

        private IEnumerable<AuthenticationSchemes> ListEnabledAuthSchemes()
        {
            // Order by strength.
            if ((_authSchemes & AuthenticationSchemes.Kerberos) == AuthenticationSchemes.Kerberos)
            {
                yield return AuthenticationSchemes.Kerberos;
            }
            if ((_authSchemes & AuthenticationSchemes.Negotiate) == AuthenticationSchemes.Negotiate)
            {
                yield return AuthenticationSchemes.Negotiate;
            }
            if ((_authSchemes & AuthenticationSchemes.NTLM) == AuthenticationSchemes.NTLM)
            {
                yield return AuthenticationSchemes.NTLM;
            }
            /*if ((_authSchemes & AuthenticationSchemes.Digest) == AuthenticationSchemes.Digest)
            {
                // TODO:
                throw new NotImplementedException("Digest challenge generation has not been implemented.");
                yield return AuthenticationSchemes.Digest;
            }*/
            if ((_authSchemes & AuthenticationSchemes.Basic) == AuthenticationSchemes.Basic)
            {
                yield return AuthenticationSchemes.Basic;
            }
        }
    }
}