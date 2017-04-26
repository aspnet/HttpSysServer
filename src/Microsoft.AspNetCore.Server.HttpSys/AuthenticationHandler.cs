// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Security.Claims;
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
        private AuthenticationScheme _scheme;

        public Task<AuthenticateResult> AuthenticateAsync()
        {
            var identity = _requestContext.User?.Identity;
            if (identity != null && identity.IsAuthenticated)
            {
                foreach (var scheme in ListEnabledAuthSchemes())
                {
                    if (string.Equals(scheme.ToString(), identity.AuthenticationType, StringComparison.Ordinal))
                    {
                        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(_requestContext.User, properties: null, authenticationScheme: _scheme.Name)));
                    }
                }
            }
            return Task.FromResult(AuthenticateResult.None());
        }

        public Task ChallengeAsync(ChallengeContext context)
        {
            switch (context.Behavior)
            {
                case ChallengeBehavior.Forbidden:
                    _requestContext.Response.StatusCode = 403;
                    break;
                case ChallengeBehavior.Unauthorized:
                    _requestContext.Response.StatusCode = 401;
                    foreach (var scheme in ListEnabledAuthSchemes())
                    {
                        _customChallenges |= scheme;
                    }
                    break;
                case ChallengeBehavior.Automatic:
                    var identity = (ClaimsIdentity)_requestContext.User?.Identity;
                    if (identity != null && identity.IsAuthenticated)
                    {
                        foreach (var scheme in ListEnabledAuthSchemes())
                        {
                            if (string.Equals(identity.AuthenticationType, scheme.ToString(), StringComparison.Ordinal))
                            {
                                _requestContext.Response.StatusCode = 403;
                                break;
                            }
                        }
                    }
                    else
                    {
                        _requestContext.Response.StatusCode = 401;
                        foreach (var scheme in ListEnabledAuthSchemes())
                        {
                            _customChallenges |= scheme;
                        }
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
            _scheme = scheme;
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