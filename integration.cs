using System;
using System.Net;
using System.Text;
using System.Web;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Security.Cryptography;
using System.Timers;
using System.Diagnostics;
using System.Globalization;

///----------------------------------------------------------------------------
///   Module:     DonationAlerts Integration for Streamer.bot
///   Based on:   play_code (https://twitch.tv/play_code)
///   Repository: (set after publishing)
///----------------------------------------------------------------------------
public class DonationAlertsSettings
{
    public string ClientId { get; set; }
    public string ClientSecret { get; set; }
    public string RedirectUrl { get; set; }
    public string Scopes { get; set; }
    public int AuthTimeoutSeconds { get; set; }
    public int ReconnectDelaySeconds { get; set; }
    public int MaxReconnectAttempts { get; set; }
    public bool DownloadAudio { get; set; }
    public string UserAgent { get; set; }
}

public class CPHInline
{
    public class PrefixedLogger
    {
        private IInlineInvokeProxy _CPH { get; set; }
        private const string Prefix = "-- Donation Alerts:";

        public PrefixedLogger(IInlineInvokeProxy _CPH)
        {
            this._CPH = _CPH;
        }
        public void WebError(WebException e)
        {
            var response = (HttpWebResponse)e.Response;
            var statusCodeResponse = response.StatusCode;
            int statusCodeResponseAsInt = ((int)response.StatusCode);
            Error("WebException with status code " + statusCodeResponseAsInt.ToString(), statusCodeResponse);
        }
        public void Error(string message)
        {
            message = string.Format("{0} {1}", Prefix, message);
            _CPH.LogWarn(message);
        }
        public void Error(string message, params Object[] additional)
        {
            string finalMessage = message;
            foreach (var line in additional)
            {
                finalMessage += ", " + line;
            }
            this.Error(finalMessage);
        }
        public void Debug(string message)
        {
            message = string.Format("{0} {1}", Prefix, message);
            _CPH.LogDebug(message);
        }
        public void Debug(string message, params Object[] additional)
        {
            string finalMessage = message;
            foreach (var line in additional)
            {
                finalMessage += ", " + line;
            }
            this.Debug(finalMessage);
        }
    }
    private Service Service { get; set; }
    private SocketService SocketService { get; set; }
    private PrefixedLogger Logger { get; set; }
    private DonationAlertsSettings Settings { get; set; }

    private const string DefaultHandlerActionName = "DonationHandler_Default";
    private const string DefaultGoalHandlerActionName = "DonationHandler_GoalHandler";
    private const string DefaultAfterDonationHandlerActionName = "DonationHandler_After";
    private const string TextPleaseConnect = "Open the authorization page and finish the flow in your browser to connect DonationAlerts."

    public void Init()
    {
        EnsureInitialized();
        TryRunUpdateChecker();
    }
    public void Dispose()
    {
        if (SocketService != null)
            SocketService.Close();
    }
    public bool Connect()
    {
        EnsureInitialized();
        if (!EnsureSettings())
            return false;

        if (!CreateAuthLink())
            return false;

        return ObtainAccessToken();
    }
    public bool Start()
    {
        EnsureInitialized();
        if (!EnsureSettings())
            return false;

        if (!EnsureAccessToken())
            return false;

        return DonationCheckerLoop();
    }
    public bool Stop()
    {
        EnsureInitialized();
        SocketService.Close();
        return true;
    }
    public bool CreateAuthLink()
    {
        EnsureInitialized();
        if (!EnsureSettings())
            return false;

        string authLink = Service.GetAuthLink();
        CPH.SendMessage(TextPleaseConnect);
        Logger.Debug("Auth link", authLink);
        OpenUrl(authLink);
        Logger.Debug("Opened URL in the default browser. Awaiting confirmation");

        return Service.ServeAndListenAuth(Settings.AuthTimeoutSeconds, delegate (string code)
        {
            CPH.SetGlobalVar("daCode", code, true);
            Logger.Debug("Authorization code received");
            return code;
        });
    }
    public bool ObtainAccessToken()
    {
        EnsureInitialized();
        if (!EnsureSettings())
            return false;

        string code = CPH.GetGlobalVar<string>("daCode");
        if (string.IsNullOrWhiteSpace(code))
        {
            Logger.Debug("Authorization code is missing. Run the connect step first.");
            return false;
        }

        var tokenData = Service.ObtainToken(code);
        if (tokenData == null)
            return false;

        StoreTokenData(tokenData);
        Logger.Debug("Access token is obtained successfully");

        return true;
    }
    public bool RefreshAccessToken()
    {
        EnsureInitialized();
        if (!EnsureSettings())
            return false;

        if (args != null && args.ContainsKey("refresh_token_recursion_protection"))
            return false;

        Logger.Debug("Refreshing access token");
        string refreshToken = CPH.GetGlobalVar<string>("daRefreshToken");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            Logger.Debug("Refresh token is missing. Run the connect step first.");
            return false;
        }

        var tokenData = Service.RefreshToken(refreshToken);
        if (tokenData == null)
            return false;

        StoreTokenData(tokenData);
        CPH.SetArgument("refresh_token_recursion_protection", true);
        return true;
    }
    public bool GetProfileInfo()
    {
        EnsureInitialized();
        return FetchProfileInfo() != null;
    }
    private void EnsureInitialized()
    {
        if (Logger == null)
            Logger = new PrefixedLogger(CPH);

        if (Settings == null)
            Settings = LoadSettings();

        if (Service == null)
            Service = new Service(new Client(Settings.UserAgent), Logger, Settings);

        if (SocketService == null)
            SocketService = new SocketService(Service, Logger, Settings);
    }
    private DonationAlertsSettings LoadSettings()
    {
        var settings = new DonationAlertsSettings();
        settings.ClientId = GetSetting("daClientId", "");
        settings.ClientSecret = GetSetting("daClientSecret", "");
        settings.RedirectUrl = NormalizeRedirectUrl(GetSetting("daRedirectUrl", "http://127.0.0.1:8554/donationalerts/callback/"));
        settings.Scopes = GetSetting("daScopes", "oauth-user-show oauth-donation-subscribe oauth-goal-subscribe");
        settings.AuthTimeoutSeconds = GetSettingInt("daAuthTimeoutSeconds", 90);
        settings.ReconnectDelaySeconds = GetSettingInt("daReconnectDelaySeconds", 5);
        settings.MaxReconnectAttempts = GetSettingInt("daMaxReconnectAttempts", 0);
        settings.DownloadAudio = GetSettingBool("daDownloadAudio", true);
        settings.UserAgent = GetSetting("daUserAgent", "StreamerBot-DonationAlerts/2.0");
        return settings;
    }
    private string GetSetting(string key, string defaultValue)
    {
        string argValue = GetArgString(key);
        if (!string.IsNullOrWhiteSpace(argValue))
            return argValue.Trim();

        string globalValue = CPH.GetGlobalVar<string>(key);
        if (!string.IsNullOrWhiteSpace(globalValue))
            return globalValue.Trim();

        return defaultValue;
    }
    private int GetSettingInt(string key, int defaultValue)
    {
        string value = GetSetting(key, "");
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;

        return defaultValue;
    }
    private bool GetSettingBool(string key, bool defaultValue)
    {
        string value = GetSetting(key, "");
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (bool.TryParse(value, out bool parsed))
            return parsed;

        return value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
    private string GetArgString(string key)
    {
        if (args == null || !args.ContainsKey(key) || args[key] == null)
            return null;

        return args[key].ToString();
    }
    private bool EnsureSettings()
    {
        Settings = Settings ?? LoadSettings();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Settings.ClientId))
            missing.Add("daClientId");
        if (string.IsNullOrWhiteSpace(Settings.ClientSecret))
            missing.Add("daClientSecret");
        if (string.IsNullOrWhiteSpace(Settings.RedirectUrl))
            missing.Add("daRedirectUrl");

        if (missing.Count > 0)
        {
            Logger.Error("Missing required settings: " + string.Join(", ", missing));
            CPH.SendMessage("DonationAlerts is not configured yet. Please set the required settings in Streamer.bot.");
            return false;
        }

        WarnIfMissingScope("oauth-user-show");
        WarnIfMissingScope("oauth-donation-subscribe");
        WarnIfMissingScope("oauth-goal-subscribe");

        return true;
    }
    private void WarnIfMissingScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(Settings.Scopes))
            return;

        if (!Settings.Scopes.Contains(scope))
            Logger.Debug("Scope is missing from daScopes", scope);
    }
    private void StoreTokenData(Service.TokenResponse tokenData)
    {
        if (!string.IsNullOrWhiteSpace(tokenData.AccessToken))
            CPH.SetGlobalVar("daAccessToken", tokenData.AccessToken, true);
        if (!string.IsNullOrWhiteSpace(tokenData.RefreshToken))
            CPH.SetGlobalVar("daRefreshToken", tokenData.RefreshToken, true);

        if (tokenData.ExpiresIn > 0)
        {
            var expiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, tokenData.ExpiresIn - 60));
            CPH.SetGlobalVar("daAccessTokenExpiresAt", expiresAt.ToString("o"), true);
        }
    }
    private bool EnsureAccessToken()
    {
        string accessToken = CPH.GetGlobalVar<string>("daAccessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Logger.Debug("Access token is missing. Run the connect step first.");
            return false;
        }

        if (!IsAccessTokenExpired())
            return true;

        Logger.Debug("Access token has expired, refreshing");
        return RefreshAccessToken();
    }
    private bool IsAccessTokenExpired()
    {
        string expiresAt = CPH.GetGlobalVar<string>("daAccessTokenExpiresAt");
        if (string.IsNullOrWhiteSpace(expiresAt))
            return false;

        if (!DateTime.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry))
            return false;

        return DateTime.UtcNow >= expiry;
    }
    private Service.ProfileInfoData FetchProfileInfo()
    {
        Logger.Debug("Fetching DonationAlerts profile data");
        if (!EnsureAccessToken())
            return null;

        string accessToken = CPH.GetGlobalVar<string>("daAccessToken");
        try
        {
            var profileInfo = Service.GetProfileInfo(accessToken);
            if (profileInfo == null)
                return null;

            CPH.SetGlobalVar("daSocketToken", profileInfo.SocketConnectionToken, true);
            CPH.SetGlobalVar("daUserId", profileInfo.Id, true);
            return profileInfo;
        }
        catch (WebException)
        {
            if (!RefreshAccessToken())
                return null;

            return FetchProfileInfo();
        }
    }
    private string NormalizeRedirectUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        return url.EndsWith("/") ? url : url + "/";
    }
    private void OpenUrl(string url)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            Process.Start(info);
        }
        catch (Exception e)
        {
            Logger.Error("Failed to open the browser", e.Message);
        }
    }
    private void TryRunUpdateChecker()
    {
        string[] candidates = new[]
        {
            "DonationAlerts | Update Checker",
            "DonationAlert Update Checker",
        };
        foreach (var actionName in candidates)
        {
            if (CPH.ActionExists(actionName))
            {
                CPH.ExecuteMethod(actionName, "CheckAndAnnounce");
                return;
            }
        }
    }
    public bool DonationCheckerLoop()
    {
        EnsureInitialized();
        if (!EnsureSettings())
            return false;

        var profileInfo = FetchProfileInfo();
        if (profileInfo == null)
        {
            CPH.SendMessage("DonationAlerts is not authorized. Run the connect step first.");
            throw new Exception("Unauthorized");
        }

        string accessToken = CPH.GetGlobalVar<string>("daAccessToken");
        SocketService = new SocketService(Service, Logger, Settings);

        SocketService
            .On(SocketService.EventStarted, delegate (string Event, Dictionary<string, string> Data)
            {
                Logger.Debug("Ready to connect to the socket");
            })
            .On(SocketService.EventConnected, delegate (string Event, Dictionary<string, string> Data)
            {
                Logger.Debug("Connected to the socket");
                CPH.SendMessage("DonationAlerts watcher is running");
            })
            .On(SocketService.EventAuthorized, delegate (string Event, Dictionary<string, string> Data)
            {
                Logger.Debug("Authorized successfully");
            })
            .On(SocketService.EventReconnected, delegate (string Event, Dictionary<string, string> Data)
            {
                Logger.Debug("Reconnected to the socket");
            })
            .On(SocketService.EventDisconnected, delegate (string Event, Dictionary<string, string> Data)
            {
                if (Data != null && Data.ContainsKey("description"))
                    Logger.Debug("Disconnected from the socket", Data["description"]);
                else
                    Logger.Debug("Disconnected from the socket");
            })
            .On(SocketService.EventAuthError, delegate (string Event, Dictionary<string, string> Data)
            {
                CPH.SendMessage("DonationAlerts authorization failed. Please reconnect.");
            })
            .On(SocketService.EventGoalUpdated, delegate (string Event, Dictionary<string, string> Data)
            {
                ExportGoal(Data);
                if (CPH.ActionExists(DefaultGoalHandlerActionName))
                    CPH.RunAction(DefaultGoalHandlerActionName);
            })
            .On(SocketService.EventRecievedDonation, delegate (string Event, Dictionary<string, string> Data)
            {
                ExportDonation(Data);
                string amountValue = Data != null && Data.ContainsKey("amount") ? Data["amount"] : "0";
                string targetActionName = string.Format("DonationHandler_{0}", FormatAmountForAction(amountValue));
                if (CPH.ActionExists(targetActionName))
                    CPH.RunAction(targetActionName, false);
                else if (CPH.ActionExists(DefaultHandlerActionName))
                    CPH.RunAction(DefaultHandlerActionName);

                if (CPH.ActionExists(DefaultAfterDonationHandlerActionName))
                    CPH.RunAction(DefaultAfterDonationHandlerActionName);
            });

        SocketService.Start(accessToken, profileInfo);
        return true;
    }

    private void ExportDonation(Dictionary<string, string> Donation)
    {
        if (Donation == null)
            return;

        string username = GetValue(Donation, "username", "Anonymous");
        string type = GetValue(Donation, "type", "");
        string message = GetValue(Donation, "message", GetValue(Donation, "content", ""));
        string amount = GetValue(Donation, "amount", "0");
        string currency = GetValue(Donation, "currency", "");

        CPH.SetArgument("daDonationId", GetValue(Donation, "id", ""));
        CPH.SetArgument("daDonationName", GetValue(Donation, "name", ""));
        CPH.SetArgument("daDonationUsername", username);
        CPH.SetArgument("daDonationMessageType", type);
        CPH.SetArgument("daDonationMessage", message);
        CPH.SetArgument("daDonationAmount", amount);
        CPH.SetArgument("daDonationCurrency", currency);
        CPH.SetArgument("daDonationIsShown", GetValue(Donation, "is_shown", ""));
        CPH.SetArgument("daDonationCreatedAt", GetValue(Donation, "created_at", ""));
        CPH.SetArgument("daDonationShownAt", GetValue(Donation, "shown_at", ""));
        CPH.SetArgument("daDonationReason", GetValue(Donation, "reason", ""));
        CPH.SetArgument("daDonationSeq", GetValue(Donation, "seq", ""));
        CPH.SetArgument("daDonationRaw", JsonConvert.SerializeObject(Donation));

        CPH.SetArgument("daName", GetValue(Donation, "name", ""));
        CPH.SetArgument("daUsername", username);
        CPH.SetArgument("daType", type);
        if (type == SocketService.DonationData.TypeText)
            CPH.SetArgument("daMessage", message);
        else if (type == SocketService.DonationData.TypeAudio)
        {
            CPH.SetArgument("daAudioUrl", message);
            if (Settings.DownloadAudio)
                CPH.SetArgument("daAudio", SaveFileToTemp(message, GetValue(Donation, "id", "audio") + ".wav"));
        }

        CPH.SetArgument("daAmount", amount);
        CPH.SetArgument("daCurrency", currency);
        CPH.SetArgument("daAmountConverted", GetValue(Donation, "amount_in_user_currency", ""));
    }

    private void ExportGoal(Dictionary<string, string> GoalData)
    {
        if (GoalData == null)
            return;

        CPH.SetArgument("daGoalId", GetValue(GoalData, "id", ""));
        CPH.SetArgument("daGoalIsActive", GetValue(GoalData, "is_active", ""));
        CPH.SetArgument("daGoalIsDefault", GetValue(GoalData, "is_default", ""));
        CPH.SetArgument("daGoalTitle", GetValue(GoalData, "title", ""));
        CPH.SetArgument("daGoalCurrency", GetValue(GoalData, "currency", ""));
        CPH.SetArgument("daGoalCurrent", GetValue(GoalData, "current", ""));
        CPH.SetArgument("daGoalTarget", GetValue(GoalData, "target", ""));
        CPH.SetArgument("daGoalReason", GetValue(GoalData, "reason", ""));
        CPH.SetArgument("daGoalSeq", GetValue(GoalData, "seq", ""));
        CPH.SetArgument("daGoalRaw", JsonConvert.SerializeObject(GoalData));
    }

    private string SaveFileToTemp(string fileUrl, string name)
    {
        if (string.IsNullOrWhiteSpace(fileUrl))
            return "";

        string targetPath = Path.Combine(Path.GetTempPath(), name);
        try
        {
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", Settings.UserAgent);
                client.DownloadFile(fileUrl, targetPath);
            }
            return targetPath;
        }
        catch (Exception e)
        {
            Logger.Error("Failed to download audio file", e.Message);
            return "";
        }
    }
    private string GetValue(Dictionary<string, string> data, string key, string fallback)
    {
        if (data != null && data.ContainsKey(key) && data[key] != null)
            return data[key];

        return fallback;
    }
    private string FormatAmountForAction(string amount)
    {
        if (decimal.TryParse(amount, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
            return parsed.ToString("0.##", CultureInfo.InvariantCulture);

        return amount;
    }
}

public class SocketService
{
    private const string SocketHost = "wss://centrifugo.donationalerts.com/connection/websocket";

    public const string EventStarted      = "started";
    public const string EventConnected    = "connected";
    public const string EventDisconnected = "disconnected";
    public const string EventReconnected  = "reconnected";
    public const string EventAuthorized   = "authorized";
    public const string EventSubscribed   = "subscribed";
    public const string EventAuthError    = "auth_error"; 

    public const string EventRecievedMessage  = "recieved_message";
    public const string EventRecievedDonation = "recieved_donation";
    public const string EventGoalUpdated      = "goal_updated";
    private EventObserver Observer { get; set; }
    private Service DaService { get; set; }
    private ClientWebSocket Socket { get; set; }
    private CPHInline.PrefixedLogger Logger { get; set; }
    private DonationAlertsSettings Settings { get; set; }

    private const int BufferSize = 8192;

    private bool StopRequested = false;
    private string PreviousGoalEventHash = "";

    public SocketService(Service service, CPHInline.PrefixedLogger logger, DonationAlertsSettings settings)
    {
        Observer = new EventObserver();
        DaService = service;
        Logger = logger;
        Settings = settings;
    }
    public SocketService On(string EventName, EventObserver.Handler handler)
    {
        Observer.Subscribe(EventName, handler);
        return this;
    }
    public Task Start(string accessToken, Service.ProfileInfoData profileInfo)
    {
        StopRequested = false;
        return ConnectLoop(accessToken, profileInfo);
    }
    public void Close()
    {
        try
        {
            StopRequested = true;
            if (Socket == null)
                return;
            Socket.Abort();
            Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).GetAwaiter().GetResult();
            Socket = null;
            Observer.Dispatch(EventDisconnected, new Dictionary<string, string> { { "description", "manual" } });
        }
        catch (Exception) { }

    }
    private Task ConnectLoop(string accessToken, Service.ProfileInfoData profileInfo)
    {
        int attempt = 0;
        while (!StopRequested)
        {
            try
            {
                ConnectAndProcess(accessToken, profileInfo, attempt > 0);
            }
            catch (WebException e)
            {
                var response = (HttpWebResponse)e.Response;
                if (response != null && response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Observer.Dispatch(EventAuthError);
                    break;
                }
                Logger.Debug("Socket loop error", e.Message);
            }
            catch (Exception e)
            {
                Logger.Debug("Socket loop error", e.Message);
            }

            if (StopRequested)
                break;

            attempt++;
            if (Settings.MaxReconnectAttempts > 0 && attempt >= Settings.MaxReconnectAttempts)
            {
                Logger.Debug("Max reconnect attempts reached");
                break;
            }

            Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, Settings.ReconnectDelaySeconds)));
        }

        return Task.CompletedTask;
    }
    private void ConnectAndProcess(string accessToken, Service.ProfileInfoData profileInfo, bool isReconnected)
    {
        Service.ProfileInfoData userProfile = profileInfo;
        if (userProfile == null || isReconnected)
            userProfile = DaService.GetProfileInfo(accessToken);

        Socket = new ClientWebSocket();
        Observer.Dispatch(EventStarted, null);
        Socket.ConnectAsync(new Uri(SocketHost), CancellationToken.None).GetAwaiter().GetResult();
        Observer.Dispatch(isReconnected ? EventReconnected : EventConnected);

        if (Socket.State == WebSocketState.Open)
        {
            string socketClientId = ObtainSocketClientId(userProfile.SocketConnectionToken);
            var channelInfoList = DaService.ObtainChannelSubscribeToken(accessToken, userProfile.Id, socketClientId);
            foreach (var channelInfoItem in channelInfoList)
            {
                SubscribeToTheChannel(channelInfoItem.Channel, channelInfoItem.Token);
            }
        }

        while (!StopRequested && Socket.State == WebSocketState.Open)
        {
            Logger.Debug("Waiting for a message");
            string rawMessage = ReceiveMessage();
            if (string.IsNullOrEmpty(rawMessage))
            {
                Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).GetAwaiter().GetResult();
                Observer.Dispatch(EventDisconnected, new Dictionary<string, string> { { "description", "closed" } });
                break;
            }

            Logger.Debug("Received message");
            Observer.Dispatch(EventRecievedMessage, new Dictionary<string, string> { { "message", rawMessage } });
            Logger.Debug(rawMessage);
            try
            {
                var socketMessage = JsonConvert.DeserializeObject<Dictionary<string, SocketMessage>>(rawMessage);
                if (socketMessage == null || !socketMessage.ContainsKey("result"))
                    continue;

                var messageType = socketMessage["result"].RecognizeType();
                switch (messageType)
                {
                    case SocketMessage.TypeDonation:
                        var donationEvent = JsonConvert.DeserializeObject<Dictionary<string, DonationResponse>>(rawMessage);
                        if (donationEvent == null || !donationEvent.ContainsKey("result") || !donationEvent["result"].IsValid())
                            continue;

                        var donation = donationEvent["result"].Data.Donation;
                        var donationPayload = donation.ToDictionary();
                        donationPayload["reason"] = donationEvent["result"].Data.Reason ?? "";
                        donationPayload["seq"] = donationEvent["result"].Data.Seq.ToString(CultureInfo.InvariantCulture);
                        Observer.Dispatch(EventRecievedDonation, donationPayload);
                        break;
                    case SocketMessage.TypeGoal:
                        var goalEvent = JsonConvert.DeserializeObject<Dictionary<string, GoalProgressResponse>>(rawMessage);
                        if (goalEvent == null || !goalEvent.ContainsKey("result") || !goalEvent["result"].IsValid())
                            continue;

                        var goal = goalEvent["result"].Data.GoalData;
                        var goalHash = Helper.GetMD5Hash(goal);

                        if (goalHash == PreviousGoalEventHash)
                        {
                            Logger.Debug("Duplicated goal data arrived. Skipping");
                            continue;
                        }

                        PreviousGoalEventHash = goalHash;

                        var goalPayload = goal.ToDictionary();
                        goalPayload["reason"] = goalEvent["result"].Data.Reason ?? "";
                        goalPayload["seq"] = goalEvent["result"].Data.Seq.ToString(CultureInfo.InvariantCulture);
                        Observer.Dispatch(EventGoalUpdated, goalPayload);
                        break;
                }

            }
            catch (Exception) { }
        }
    }

    private string ReceiveMessage()
    {
        var buffer = new ArraySegment<byte>(new byte[BufferSize]);
        using (var ms = new MemoryStream())
        {
            WebSocketReceiveResult result;
            do
            {
                result = Socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                ms.Write(buffer.Array, 0, result.Count);
            } while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
    private string ObtainSocketClientId(string socketToken)
    {
        if (Socket == null || Socket.State != WebSocketState.Open)
            throw new Exception("Socket is closed");

        var request = new SocketClientRequest
        {
            Parameters = new SocketClientRequestParams
            {
                Token = socketToken,
            }
        };
        var payload = JsonConvert.SerializeObject(request);

        Socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        ).GetAwaiter().GetResult();

        var rawMessage = ReceiveMessage();
        if (string.IsNullOrEmpty(rawMessage))
        {
            Observer.Dispatch(EventDisconnected, new Dictionary<string, string> { { "description", "auth" } });
            throw new Exception("Socket has closed connection");
        }
        Logger.Debug("Auth response received");

        var authResponse = JsonConvert.DeserializeObject<SocketClientResponse>(rawMessage);
        if (authResponse == null || authResponse.Data == null)
            throw new Exception("Socket auth response is invalid");

        Observer.Dispatch(EventAuthorized, new Dictionary<string, string> { { "client", authResponse.Data.Client }, { "version", authResponse.Data.Version } });

        return authResponse.Data.Client;
    }
    private void SubscribeToTheChannel(string channel, string channelToken)
    {
        var request = new SubscribeRequest
        {
            Data = new SubscribeRequestData { Channel = channel, Token = channelToken },
        };
        var payload = JsonConvert.SerializeObject(request);
        Socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        ).GetAwaiter().GetResult();
        Logger.Debug("Subscribe request has been sent");

        for (int i = 0; i < 4; i++)
        {
            var rawMessage = ReceiveMessage();
            if (string.IsNullOrEmpty(rawMessage))
                throw new Exception("Socket has closed connection");

            Logger.Debug("Subscribe response", rawMessage);
            var response = JsonConvert.DeserializeObject<SubscribeResponse>(rawMessage);
            if (response == null || response.Data == null)
                continue;

            if (response.Id != 0 && response.Id != request.Id)
                continue;

            if (response.Data.Channel != channel)
                continue;

            break;
        }

        Logger.Debug("Subscribed to the channel", channel);
        Observer.Dispatch(EventSubscribed, new Dictionary<string, string> { { "channel", channel } });
    }

    private class SocketClientRequest
    {
        [JsonProperty("id")]
        public int Id = 1;
        [JsonProperty("params")]
        public SocketClientRequestParams Parameters { get; set; }
    }
    private class SocketClientRequestParams
    {
        [JsonProperty("token")]
        public string Token { get; set; }
    }
    private class SocketClientResponse
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("result")]
        public SocketClientResponseData Data { get; set; }
    }
    private class SocketClientResponseData
    {
        [JsonProperty("client")]
        public string Client;
        [JsonProperty("version")]
        public string Version;
    }
    private class SubscribeRequest
    {
        [JsonProperty("id")]
        public int Id = 2;
        [JsonProperty("method")]
        public int Method = 1;
        [JsonProperty("params")]
        public SubscribeRequestData Data { get; set; }
    }
    private class SubscribeRequestData
    {
        [JsonProperty("channel")]
        public string Channel { get; set; }
        [JsonProperty("token")]
        public string Token { get; set; }
    }
    private class SubscribeResponse
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("result")]
        public SubscribeResponseData Data { get; set; }
    }
    private class SubscribeResponseData
    {
        [JsonProperty("recoverable")]
        public bool IsRecoverable = false;
        [JsonProperty("seq")]
        public int seq = 0;
        [JsonProperty("type")]
        public int Type { get; set; }
        [JsonProperty("channel")]
        public string Channel { get; set; }
    }
    private class SocketMessage
    {
        public const string TypeDonation = "donation";
        public const string TypeGoal = "goal";
        public const string TypeUnknown = "unknown";

        [JsonProperty("channel")]
        public string Channel { get; set; }
        public string RecognizeType()
        {
            if (string.IsNullOrWhiteSpace(Channel))
                return TypeUnknown;

            if (Channel.Contains("$alerts:donation"))
            {
                return TypeDonation;
            }
            else if (Channel.Contains("$goals:goal"))
            {
                return TypeGoal;
            }
            else
            {
                return TypeUnknown;
            }
        }
    }
    private class DonationResponse
    {
        [JsonProperty("channel")]
        public string Channel { get; set; }
        [JsonProperty("data")]
        public DonationResponseData Data = new DonationResponseData();

        public bool IsValid()
        {
            return Data != null && Data.Donation != null && Data.Donation.Amount > 0;
        }
    }
    private class DonationResponseData
    {
        [JsonProperty("seq")]
        public int Seq { get; set; }
        [JsonProperty("reason")]
        public string Reason { get; set; }
        [JsonProperty("data")]
        public DonationData Donation = new DonationData();
    }
    public class DonationData
    {
        public const string TypeAudio = "audio";
        public const string TypeText = "text";

        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("username")]
        public string UserName { get; set; }
        [JsonProperty("message_type")]
        public string Type { get; set; }
        [JsonProperty("message")]
        public string Message { get; set; }
        [JsonProperty("amount")]
        public double Amount { get; set; }
        [JsonProperty("currency")]
        public string Currency { get; set; }
        [JsonProperty("amount_in_user_currency")]
        public double AmountInUserCurrency { get; set; }
        [JsonProperty("is_shown")]
        public int IsShown { get; set; }
        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }
        [JsonProperty("shown_at")]
        public string ShownAt { get; set; }

        public Dictionary<string, string> ToDictionary() => new Dictionary<string, string>
            {
                { "id", Id.ToString(CultureInfo.InvariantCulture) },
                { "name", Name ?? "" },
                { "username", UserName ?? "" },
                { "type", Type ?? "" },
                { "message", Message ?? "" },
                { "content", Message ?? "" },
                { "amount", Amount.ToString(CultureInfo.InvariantCulture) },
                { "currency", Currency ?? "" },
                { "amount_in_user_currency", AmountInUserCurrency.ToString(CultureInfo.InvariantCulture) },
                { "is_shown", IsShown.ToString(CultureInfo.InvariantCulture) },
                { "created_at", CreatedAt ?? "" },
                { "shown_at", ShownAt ?? "" },
            };
    }
    private class GoalProgressResponse
    {
        [JsonProperty("channel")]
        public string Channel { get; set; }
        [JsonProperty("data")]
        public GoalProgressData Data = new GoalProgressData();
        public bool IsValid()
        {
            return Data != null && Data.GoalData != null && Data.GoalData.GoalAmount > 0;
        }
    }
    private class GoalProgressData
    {
        [JsonProperty("seq")]
        public int Seq { get; set; }
        [JsonProperty("reason")]
        public string Reason { get; set; }
        [JsonProperty("data")]
        public GoalData GoalData = new GoalData();
    }
    private class GoalData
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("is_active")]
        public bool IsActive { get; set; }
        [JsonProperty("is_default")]
        public bool IsDefault { get; set; }
        [JsonProperty("title")]
        public string Title { get; set; }
        [JsonProperty("currency")]
        public string Currency { get; set; }
        [JsonProperty("raised_amount")]
        public double RaisedAmount { get; set; }
        [JsonProperty("goal_amount")]
        public double GoalAmount { get; set; }

        public Dictionary<string, string> ToDictionary()
        {
            return new Dictionary<string, string>
            {
                { "id", Id.ToString(CultureInfo.InvariantCulture) },
                { "is_active", IsActive.ToString() },
                { "is_default", IsDefault.ToString() },
                { "title", Title ?? "" },
                { "currency", Currency ?? "" },
                { "current", RaisedAmount.ToString(CultureInfo.InvariantCulture) },
                { "target", GoalAmount.ToString(CultureInfo.InvariantCulture) },
            };
        }
    }
    
}
public class Service
{
    public delegate string HandleCode(string code);

    private const string EndpointAuthorize = "/oauth/authorize";
    private const string EndpointToken = "/oauth/token";
    private const string EndpointProfileInfo = "/api/v1/user/oauth";
    private const string EndpointSubscribe = "/api/v1/centrifuge/subscribe";

    private CPHInline.PrefixedLogger Logger { get; set; }
    private Client Client { get; set; }
    private DonationAlertsSettings Settings { get; set; }
    private HttpListener Listener = null;

    public Service(Client client, CPHInline.PrefixedLogger logger, DonationAlertsSettings settings)
    {
        Client = client;
        Logger = logger;
        Settings = settings;
    }
    public bool ServeAndListenAuth(int timeoutSeconds, HandleCode Handler)
    {
        try
        {
            Listener = new HttpListener();
            Listener.Prefixes.Add(Settings.RedirectUrl);
            Listener.Start();
            Func<Task> func = new Func<Task>(async () => {
                bool runServer = true;
                while (runServer)
                {
                    Logger.Debug("Server is waiting ...");
                    HttpListenerContext context = await Listener.GetContextAsync();
                    HttpListenerRequest request = context.Request;
                    HttpListenerResponse resp = context.Response;
                    var queryDictionary = HttpUtility.ParseQueryString(request.Url.Query);
                    string code = queryDictionary["code"];
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        WriteHtmlResponse(resp, BuildErrorPage("Authorization code is missing."));
                        runServer = false;
                        Listener.Close();
                        continue;
                    }

                    Handler(code);
                    WriteHtmlResponse(resp, BuildSuccessPage());

                    runServer = false;
                    Listener.Close();
                }
            });
            Task listenTask = func.Invoke();
            var forceCloseTimer = new System.Timers.Timer(Math.Max(5, timeoutSeconds) * 1000);
            forceCloseTimer.Elapsed += ForceCloseServer;
            forceCloseTimer.AutoReset = false;
            forceCloseTimer.Enabled = true;

            listenTask.GetAwaiter().GetResult();
            Listener.Close();
        }
        catch (WebException e)
        {
            Logger.Debug(e.Status.ToString());
            return false;
        }
        catch (HttpListenerException e)
        {
            Logger.Debug(e.Message);
            return false;
        }

        return true;
    }
    public string GetAuthLink()
    {
        string url = Client.TargetHost + EndpointAuthorize;
        return string.Format("{0}?client_id={1}&redirect_uri={2}&scope={3}&response_type=code", url, Settings.ClientId, GetEncodedRedirectUri(), HttpUtility.UrlEncode(Settings.Scopes));
    }
    public ProfileInfoData GetProfileInfo(string accessToken)
    {
        var headers = new Dictionary<string, string>
        {
            { "Authorization", "Bearer " + accessToken }
        };

        try
        {
            string json = Client.GET(EndpointProfileInfo, new Dictionary<string, string>(), headers);
            ProfileInfoResponse profile = JsonConvert.DeserializeObject<ProfileInfoResponse>(json);
            if (profile == null || profile.Data == null)
            {
                Logger.Error("Profile response is invalid");
                return null;
            }
            Logger.Debug("Retrieved user profile info", profile.Data.Id, json);

            return profile.Data;
        }
        catch (WebException e)
        {
            Logger.WebError(e);
            throw e;
        }
    }
    public TokenResponse ObtainToken(string code)
    {
        var values = new Dictionary<string, string>
        {
            { "grant_type", "authorization_code" },
            { "client_id", Settings.ClientId },
            { "client_secret", Settings.ClientSecret },
            { "code", code },
            { "redirect_uri", Settings.RedirectUrl }
        };

        try
        {
            string json = Client.POSTForm(EndpointToken, values);
            Logger.Debug("Request to the token API has been performed");
            TokenResponse tokenData = JsonConvert.DeserializeObject<TokenResponse>(json);
            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
            {
                Logger.Error("Token response is invalid");
                return null;
            }

            return tokenData;
        }
        catch (WebException e)
        {
            Logger.WebError(e);
            return null;
        }
    }
    public TokenResponse RefreshToken(string refreshToken)
    {
        var values = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "client_id", Settings.ClientId },
            { "client_secret", Settings.ClientSecret },
            { "refresh_token", refreshToken },
            { "scope", Settings.Scopes }
        };

        try
        {
            string json = Client.POSTForm(EndpointToken, values);
            Logger.Debug("Request to the token API has been performed");
            TokenResponse tokenData = JsonConvert.DeserializeObject<TokenResponse>(json);
            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
            {
                Logger.Error("Token response is invalid");
                return null;
            }

            return tokenData;
        }
        catch (WebException e)
        {
            Logger.WebError(e);
            return null;
        }
    }
    public List<ChannelSubscribeResponse> ObtainChannelSubscribeToken(string AccessToken, int UserId, string SocketClientId)
    {
        var request = new ChannelSubscribeRequest
        {
            Client = SocketClientId,
            Channels = new List<string>() { 
                { string.Format("$alerts:donation_{0}", UserId) },
                { string.Format("$goals:goal_{0}", UserId) },
            }
        };
        string payload = JsonConvert.SerializeObject(request);

        var headers = new Dictionary<string, string>
        {
            { "Authorization", "Bearer " + AccessToken }
        };

        try
        {
            var response = Client.POST(EndpointSubscribe, payload, headers);
            var channels = JsonConvert.DeserializeObject<Dictionary<string, List<ChannelSubscribeResponse>>>(response);
            if (channels == null || !channels.ContainsKey("channels") || channels["channels"].Count == 0)
                throw new Exception("Cannot fetch channels and its tokens");

            return channels["channels"];
        }
        catch (WebException e)
        {
            Logger.WebError(e);
            throw e;
        }
    }

    private string GetEncodedRedirectUri()
    {
        return HttpUtility.UrlEncode(Settings.RedirectUrl);
    }
    private void ForceCloseServer(Object source, ElapsedEventArgs e)
    {
        Logger.Debug("Timer invoked");
        if (Listener != null && Listener.IsListening)
        {
            Listener.Close();
            Logger.Debug("Server has been closed by timeout");
        }
    }
    private void WriteHtmlResponse(HttpListenerResponse resp, string html)
    {
        byte[] data = Encoding.UTF8.GetBytes(html ?? string.Empty);
        resp.ContentType = "text/html";
        resp.ContentEncoding = Encoding.UTF8;
        resp.ContentLength64 = data.LongLength;
        resp.OutputStream.Write(data, 0, data.Length);
        resp.Close();
    }
    private string BuildSuccessPage()
    {
        return @"<!doctype html>
<html lang='en'>
<head>
  <meta charset='UTF-8'>
  <meta name='viewport' content='width=device-width,initial-scale=1'>
  <title>DonationAlerts connected</title>
  <link rel='preconnect' href='https://fonts.googleapis.com'>
  <link rel='preconnect' href='https://fonts.gstatic.com' crossorigin>
  <link href='https://fonts.googleapis.com/css2?family=Fraunces:opsz,wght@9..144,600;9..144,700&family=IBM+Plex+Sans:wght@400;500;600&display=swap' rel='stylesheet'>
  <style>
    :root {
      --paper: #f7f0e6;
      --sand: #efe1cf;
      --ink: #1b1a17;
      --muted: #6c6155;
      --accent: #ff7a59;
      --accent-soft: #ffd7b8;
      --card: #fffaf2;
      --shadow: 0 20px 60px rgba(33, 27, 20, 0.15);
      --border: 2px solid #1b1a17;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      font-family: 'IBM Plex Sans', sans-serif;
      background: radial-gradient(circle at 10% 20%, #fff1dd 0%, transparent 55%),
                  radial-gradient(circle at 90% 10%, #ffe4c4 0%, transparent 45%),
                  linear-gradient(135deg, var(--paper), var(--sand));
      color: var(--ink);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 32px;
    }
    .wrap {
      width: min(680px, 100%);
      background: var(--card);
      border: var(--border);
      box-shadow: var(--shadow);
      padding: 36px 40px;
      position: relative;
      overflow: hidden;
      animation: rise 600ms ease-out;
    }
    .wrap::before {
      content: '';
      position: absolute;
      inset: -80px 40% auto auto;
      width: 240px;
      height: 240px;
      background: radial-gradient(circle, var(--accent-soft), transparent 70%);
      opacity: 0.7;
    }
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      padding: 8px 14px;
      border: 1px solid var(--ink);
      border-radius: 999px;
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: 1.4px;
      background: #fff2e5;
    }
    h1 {
      font-family: 'Fraunces', serif;
      font-size: clamp(28px, 3vw, 40px);
      margin: 20px 0 12px;
    }
    p {
      margin: 0 0 16px;
      color: var(--muted);
      line-height: 1.6;
    }
    .steps {
      margin-top: 18px;
      padding: 16px 18px;
      border: 1px dashed #c6b8a7;
      background: #fff6ec;
    }
    .steps strong {
      color: var(--ink);
    }
    .footer {
      margin-top: 24px;
      font-size: 13px;
      color: var(--muted);
    }
    @keyframes rise {
      from { opacity: 0; transform: translateY(16px); }
      to { opacity: 1; transform: translateY(0); }
    }
  </style>
</head>
<body>
  <main class='wrap'>
    <div class='badge'>Connection complete</div>
    <h1>DonationAlerts is linked.</h1>
    <p>You can close this tab and return to Streamer.bot.</p>
    <div class='steps'>
      <p><strong>Next:</strong> run your <strong>DA | Start</strong> action to begin listening for alerts.</p>
      <p>If you created chat commands, try <strong>!da_start</strong> or your custom trigger.</p>
    </div>
    <div class='footer'>Need to reconnect? Use the <strong>DA | Connect</strong> action again.</div>
  </main>
</body>
</html>";
    }
    private string BuildErrorPage(string message)
    {
        return string.Format(@"<!doctype html>
<html lang='en'>
<head>
  <meta charset='UTF-8'>
  <meta name='viewport' content='width=device-width,initial-scale=1'>
  <title>DonationAlerts error</title>
  <link rel='preconnect' href='https://fonts.googleapis.com'>
  <link rel='preconnect' href='https://fonts.gstatic.com' crossorigin>
  <link href='https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;600&display=swap' rel='stylesheet'>
  <style>
    body {
      font-family: 'IBM Plex Sans', sans-serif;
      background: #fff3f0;
      color: #3b1f1a;
      padding: 40px;
      margin: 0;
    }
    .box {
      max-width: 640px;
      margin: 0 auto;
      background: #ffffff;
      border: 2px solid #3b1f1a;
      padding: 24px;
      box-shadow: 0 16px 40px rgba(59, 31, 26, 0.12);
    }
  </style>
</head>
<body>
  <div class='box'>
    <h1>Connection error</h1>
    <p>{0}</p>
    <p>Please return to Streamer.bot and try again.</p>
  </div>
</body>
</html>", message);
    }

    public class TokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }
        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }
        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }
    }
    public class ProfileInfoResponse
    {
        [JsonProperty("data")]
        public ProfileInfoData Data;
    }
    public class ProfileInfoData
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("code")]
        public string Code { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("avatar")]
        public string Avatar { get; set; }
        [JsonProperty("email")]
        public string Email { get; set; }
        [JsonProperty("socket_connection_token")]
        public string SocketConnectionToken;
    }
    public class ChannelSubscribeResponse
    {
        [JsonProperty("channel")]
        public string Channel;
        [JsonProperty("token")]
        public string Token;
    }
    public class ChannelSubscribeRequest
    {
        [JsonProperty("client")]
        public string Client { get; set; }
        [JsonProperty("channels")]
        public List<string> Channels { get; set; }
    }
}
public class Client
{
    public const string TargetHost = "https://www.donationalerts.com";
    private string UserAgent { get; set; }

    public Client(string userAgent)
    {
        UserAgent = string.IsNullOrWhiteSpace(userAgent) ? "StreamerBot-DonationAlerts/2.0" : userAgent;
    }

    public string GET(string endpoint, Dictionary<string, string> parameters, Dictionary<string, string> headers)
    {
        var queryParams = new List<string>();
        foreach (var parameter in parameters)
        {
            queryParams.Add(string.Format("{0}={1}", HttpUtility.UrlEncode(parameter.Key), HttpUtility.UrlEncode(parameter.Value)));
        }
        if (queryParams.Count > 0)
            endpoint += "?" + string.Join("&", queryParams);

        return Perform(WebRequestMethods.Http.Get, TargetHost + endpoint, string.Empty, headers, "application/json");
    }
    public string GET(string endpoint, Dictionary<string, string> parameters)
    {
        return GET(endpoint, parameters, new Dictionary<string, string>());
    }
    public string GET(string endpoint)
    {
        return GET(endpoint, new Dictionary<string, string>());
    }
    public string POST(string endpoint, string payload, Dictionary<string, string> headers)
    {
        return Perform(WebRequestMethods.Http.Post, TargetHost + endpoint, payload, headers, "application/json");
    }
    public string POST(string endpoint, Dictionary<string, string> payload, Dictionary<string, string> headers)
    {
        string payloadString = payload != null && payload.Count > 0 ? JsonConvert.SerializeObject(payload) : string.Empty;
        return POST(endpoint, payloadString, headers);
    }
    public string POST(string endpoint, Dictionary<string, string> payload)
    {
        return POST(endpoint, payload, new Dictionary<string, string>());
    }
    public string POST(string endpoint)
    {
        return POST(endpoint, new Dictionary<string, string>());
    }
    public string POSTForm(string endpoint, Dictionary<string, string> payload)
    {
        return POSTForm(endpoint, payload, new Dictionary<string, string>());
    }
    public string POSTForm(string endpoint, Dictionary<string, string> payload, Dictionary<string, string> headers)
    {
        string payloadString = BuildFormBody(payload);
        return Perform(WebRequestMethods.Http.Post, TargetHost + endpoint, payloadString, headers, "application/x-www-form-urlencoded");
    }

    private string BuildFormBody(Dictionary<string, string> payload)
    {
        if (payload == null || payload.Count == 0)
            return string.Empty;

        var pairs = new List<string>();
        foreach (var item in payload)
        {
            pairs.Add(string.Format("{0}={1}", HttpUtility.UrlEncode(item.Key), HttpUtility.UrlEncode(item.Value)));
        }
        return string.Join("&", pairs);
    }

    private string Perform(string method, string url, string payload, Dictionary<string, string> headers, string contentType)
    {
        HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(url);
        webRequest.Method = method;
        webRequest.ContentType = contentType;
        webRequest.UserAgent = UserAgent;
        webRequest.Accept = "application/json";

        if (headers != null)
        {
            foreach (var header in headers)
            {
                webRequest.Headers.Set(header.Key, header.Value);
            }
        }

        if (!string.IsNullOrEmpty(payload))
        {
            byte[] requestBytes = Encoding.UTF8.GetBytes(payload);
            webRequest.ContentLength = requestBytes.Length;
            using (Stream requestStream = webRequest.GetRequestStream())
            {
                requestStream.Write(requestBytes, 0, requestBytes.Length);
            }
        }

        var response = (HttpWebResponse)webRequest.GetResponse();
        string json = "";
        using (Stream respStr = response.GetResponseStream())
        {
            using (StreamReader rdr = new StreamReader(respStr, Encoding.UTF8))
            {
                json = rdr.ReadToEnd();
            }
        }

        return json;
    }
}
public class EventObserver
{
    public delegate void Handler(string Event, Dictionary<string, string> Data = null);
    private Dictionary<string, List<Handler>> Handlers { get; set; }

    public EventObserver()
    {
        Handlers = new Dictionary<string, List<Handler>>();
    }
    public EventObserver Subscribe(string EventName, Handler handler)
    {
        if (!Handlers.ContainsKey(EventName))
            Handlers.Add(EventName, new List<Handler>());

        Handlers[EventName].Add(handler);
        return this;
    }
    public void Dispatch(string EventName, Dictionary<string, string> Data = null)
    {
        if (!Handlers.ContainsKey(EventName) || Handlers[EventName].Count == 0)
            return;

        foreach (var handler in Handlers[EventName])
        {
            handler(EventName, Data);
        }
    }
}

public static class Helper
{
    public static string GetMD5Hash(object obj)
    {
        using (var md5 = MD5.Create())
        {
            var json = JsonConvert.SerializeObject(obj);
            var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(json));
            var sb = new StringBuilder();
            foreach (var b in hashBytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}











