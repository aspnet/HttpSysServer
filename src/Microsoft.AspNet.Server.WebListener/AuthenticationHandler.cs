﻿// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING
// WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF
// TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR
// NON-INFRINGEMENT.
// See the Apache 2 License for the specific language governing
// permissions and limitations under the License.

// -----------------------------------------------------------------------
// <copyright file="AuthenticationManager.cs" company="Microsoft">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNet.Http.Interfaces.Security;
using Microsoft.Net.Http.Server;

namespace Microsoft.AspNet.Server.WebListener
{
    internal class AuthenticationHandler : IAuthenticationHandler
    {
        private RequestContext _requestContext;
        private AuthenticationSchemes _authTypes;
        private AuthenticationSchemes _customChallenges;

        internal AuthenticationHandler(RequestContext requestContext)
        {
            _requestContext = requestContext;
            _authTypes = requestContext.AuthenticationChallenges;
            _customChallenges = AuthenticationSchemes.None;
        }

        public void Authenticate(IAuthenticateContext context)
        {
            var user = _requestContext.User;
            var identity = user == null ? null : (ClaimsIdentity)user.Identity;

            foreach (var authType in ListEnabledAuthTypes())
            {
                string authScheme = authType.ToString();
                if (context.AuthenticationSchemes.Contains(authScheme, StringComparer.Ordinal))
                {
                    if (identity != null && identity.IsAuthenticated
                                             && string.Equals(authScheme, identity.AuthenticationType, StringComparison.Ordinal))
                    {
                        context.Authenticated(new ClaimsPrincipal(user.Identity), properties: null, description: GetDescription(authScheme));
                    }
                    else
                    {
                        context.NotAuthenticated(authScheme, properties: null, description: GetDescription(authScheme));
                    }
                }
            }
        }

        public Task AuthenticateAsync(IAuthenticateContext context)
        {
            Authenticate(context);
            return Task.FromResult(0);
        }

        public void Challenge(IChallengeContext context)
        {
            foreach (var authType in ListEnabledAuthTypes())
            {
                var authScheme = authType.ToString();
                // Not including any auth types means it's a blanket challenge for any auth type.
                if (context.AuthenticationSchemes == null || !context.AuthenticationSchemes.Any()
                    || context.AuthenticationSchemes.Contains(authScheme, StringComparer.Ordinal))
                {
                    _customChallenges |= authType;
                    context.Accept(authScheme, GetDescription(authScheme));
                }
            }
            // A challenge was issued, it overrides any pre-set auth types.
            _requestContext.AuthenticationChallenges = _customChallenges;
        }

        public void GetDescriptions(IDescribeSchemesContext context)
        {
            // TODO: Caching, this data doesn't change per request.
            foreach (var authType in ListEnabledAuthTypes())
            {
                context.Accept(GetDescription(authType.ToString()));
            }
        }

        public void SignIn(ISignInContext context)
        {
            // Not supported
        }

        public void SignOut(ISignOutContext context)
        {
            // Not supported
        }

        private IDictionary<string, object> GetDescription(string authenticationScheme)
        {
            return new Dictionary<string, object>()
            {
                { "AuthenticationScheme", authenticationScheme },
                { "Caption", "Windows:" + authenticationScheme },
            };
        }

        private IEnumerable<AuthenticationSchemes> ListEnabledAuthTypes()
        {
            // Order by strength.
            if ((_authTypes & AuthenticationSchemes.Kerberos) == AuthenticationSchemes.Kerberos)
            {
                yield return AuthenticationSchemes.Kerberos;
            }
            if ((_authTypes & AuthenticationSchemes.Negotiate) == AuthenticationSchemes.Negotiate)
            {
                yield return AuthenticationSchemes.Negotiate;
            }
            if ((_authTypes & AuthenticationSchemes.NTLM) == AuthenticationSchemes.NTLM)
            {
                yield return AuthenticationSchemes.NTLM;
            }
            /*if ((_authTypes & AuthenticationSchemes.Digest) == AuthenticationSchemes.Digest)
            {
                // TODO:
                throw new NotImplementedException("Digest challenge generation has not been implemented.");
                yield return AuthenticationSchemes.Digest;
            }*/
            if ((_authTypes & AuthenticationSchemes.Basic) == AuthenticationSchemes.Basic)
            {
                yield return AuthenticationSchemes.Basic;
            }
        }
    }
}