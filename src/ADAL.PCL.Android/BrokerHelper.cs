//----------------------------------------------------------------------
// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
// Apache License 2.0
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Accounts;
using Android.App;
using Android.Content;
using Java.IO;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Interfaces;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory
{
    class BrokerHelper : IBrokerHelper
    {
        private static SemaphoreSlim readyForResponse = null;
        private static AuthenticationResultEx resultEx = null;

        private BrokerProxy mBrokerProxy = new BrokerProxy(Application.Context);

        public IPlatformParameters PlatformParameters { get; set; }

        private bool WillSkipBroker()
        {
            PlatformParameters pp = PlatformParameters as PlatformParameters;
            if (pp != null)
            {
                return pp.SkipBroker;
            }

            return true;
        }

        //TODO - enable broker flows when authenticator apps support it
        public bool CanInvokeBroker { get { return false && !WillSkipBroker() && mBrokerProxy.CanSwitchToBroker(); } }


        public async Task<AuthenticationResultEx> AcquireTokenUsingBroker(IDictionary<string, string> brokerPayload)
        {
            resultEx = null;
            readyForResponse = new SemaphoreSlim(0);
            try
            {
                await Task.Run(() => AcquireToken(brokerPayload)).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                PlatformPlugin.Logger.Error(null, exc);
                throw;
            }
            await readyForResponse.WaitAsync().ConfigureAwait(false);
            return resultEx;
        }
        public void AcquireToken(IDictionary<string, string> brokerPayload)
        {

            if (brokerPayload.ContainsKey("broker_install_url"))
            {
                string url = brokerPayload["broker_install_url"];
                Uri uri = new Uri(url);
                string query = uri.Query;
                if (query.StartsWith("?"))
                {
                    query = query.Substring(1);
                }

                Dictionary<string, string> keyPair = EncodingHelper.ParseKeyValueList(query, '&', true, false, null);

                PlatformParameters pp = PlatformParameters as PlatformParameters;
                pp.CallerActivity.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(keyPair["app_link"])));
                
                throw new MsalException(AdalErrorAndroidEx.BrokerApplicationRequired, AdalErrorMessageAndroidEx.BrokerApplicationRequired);
            }

            Context mContext = Application.Context;
            AuthenticationRequest request = new AuthenticationRequest(brokerPayload);
            PlatformParameters platformParams = PlatformParameters as PlatformParameters;

            // BROKER flow intercepts here
            // cache and refresh call happens through the authenticator service
            if (mBrokerProxy.VerifyUser(request.LoginHint,
                request.UserId))
            {
                PlatformPlugin.Logger.Verbose(null, "It switched to broker for context: " + mContext.PackageName);
                request.BrokerAccountName = request.LoginHint;

                // Don't send background request, if prompt flag is always or
                // refresh_session
                if (!string.IsNullOrEmpty(request.BrokerAccountName) || !string.IsNullOrEmpty(request.UserId))
                {
                    PlatformPlugin.Logger.Verbose(null, "User is specified for background token request");
                    resultEx = mBrokerProxy.GetAuthTokenInBackground(request, platformParams.CallerActivity);
                }
                else
                {
                    PlatformPlugin.Logger.Verbose(null, "User is not specified for background token request");
                }

                if (resultEx != null && !string.IsNullOrEmpty(resultEx.Result.AccessToken))
                {
                    PlatformPlugin.Logger.Verbose(null, "Token is returned from background call ");
                    readyForResponse.Release();
                    return;
                }

                // Launch broker activity
                // if cache and refresh request is not handled.
                // Initial request to authenticator needs to launch activity to
                // record calling uid for the account. This happens for Prompt auto
                // or always behavior.
                PlatformPlugin.Logger.Verbose(null, "Token is not returned from backgroud call");

                // Only happens with callback since silent call does not show UI
                PlatformPlugin.Logger.Verbose(null, "Launch activity for Authenticator");
                PlatformPlugin.Logger.Verbose(null, "Starting Authentication Activity");
                if (resultEx == null)
                {
                    PlatformPlugin.Logger.Verbose(null, "Initial request to authenticator");
                    // Log the initial request but not force a prompt
                }

                if (brokerPayload.ContainsKey("silent_broker_flow"))
                {
                    throw new MsalSilentTokenAcquisitionException();
                }

                // onActivityResult will receive the response
                // Activity needs to launch to record calling app for this
                // account
                Intent brokerIntent = mBrokerProxy.GetIntentForBrokerActivity(request, platformParams.CallerActivity);
                if (brokerIntent != null)
                {
                    try
                    {
                        PlatformPlugin.Logger.Verbose(null, "Calling activity pid:" + Android.OS.Process.MyPid()
                                                            + " tid:" + Android.OS.Process.MyTid() + "uid:"
                                                            + Android.OS.Process.MyUid());
                        platformParams.CallerActivity.StartActivityForResult(brokerIntent, 1001);
                    }
                    catch (ActivityNotFoundException e)
                    {
                        PlatformPlugin.Logger.Error(null, e);
                    }
                }
            }
            else
            {
                throw new MsalException(AdalErrorAndroidEx.NoBrokerAccountFound, "Add requested account as a Workplace account via Settings->Accounts or set SkipBroker=false.");
            }
        }
        
        internal static void SetBrokerResult(Intent data, int resultCode)
        {
            if (resultCode != BrokerResponseCode.ResponseReceived)
            {
                resultEx = new AuthenticationResultEx
                {
                    Exception = new MsalException(MsalError.AuthenticationCanceled, MsalErrorMessage.AuthenticationCanceled)
                };
            }
            else
            {
                string accessToken = data.GetStringExtra("account.access.token");
                DateTimeOffset expiresOn = BrokerProxy.ConvertFromTimeT(data.GetLongExtra("account.expiredate", 0));
                User userInfo = BrokerProxy.GetUserInfoFromBrokerResult(data.Extras);
                resultEx = new AuthenticationResultEx
                {
                    Result = new AuthenticationResult("Bearer", accessToken, expiresOn)
                    {
                        User = userInfo
                    }
                };
            }
            readyForResponse.Release();
        }
    }

    internal class CallBackHandler : Java.Lang.Object, IAccountManagerCallback
    {
        public void Run(IAccountManagerFuture future)
        {
        }
    }
}