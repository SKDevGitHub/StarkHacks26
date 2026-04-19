using System;
using System.Collections;
using System.Reflection;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public sealed class WebRtcHttpReceiver : MonoBehaviour
{
    [Header("Signaling")]
    [SerializeField] string signalingUrl = "https://10.10.9.81:8080";
    [SerializeField] bool autoConnect = true;
    [SerializeField] bool sendOfferAsJson;
    [SerializeField] bool waitForIceGatheringComplete = true;
    [SerializeField] float iceGatheringTimeoutSeconds = 5f;
    [SerializeField] bool acceptSelfSignedCertificates = true;

    [Header("Output")]
    [SerializeField] RawImage targetRawImage;
    [SerializeField] Renderer targetRenderer;
    [SerializeField] string targetTextureProperty = "_BaseMap";
    [SerializeField] bool applyCommonTextureProperties = true;
    [SerializeField] RenderTexture outputTexture;
    [SerializeField] bool renderReceivedTextureDirectly;
    [SerializeField] bool logFrameCopyHeartbeat;
    [SerializeField] bool logInboundStats = true;
    [SerializeField] bool pumpReceivedTextureInUpdate = true;
    [SerializeField] bool reapplyDisplayTextureEveryFrame = true;

    [Header("Connection")]
    [SerializeField] bool useGoogleStun = true;
    [SerializeField] bool preferVp8 = true;

    RTCPeerConnection peerConnection;
    Coroutine webRtcUpdateCoroutine;
    Coroutine connectCoroutine;
    Coroutine frameCopyCoroutine;
    Coroutine statsCoroutine;
    VideoStreamTrack receivedVideoTrack;
    Texture receivedTexture;
    bool ownsOutputTexture;
    bool loggedFirstFrame;
    float nextHeartbeatTime;
    MaterialPropertyBlock propertyBlock;
    Texture lastAppliedTexture;
    Material targetMaterialInstance;

    public RenderTexture OutputTexture => outputTexture;
    public Texture ReceivedTexture => receivedTexture;

    void Start()
    {
        webRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());

        if (autoConnect)
            Connect();
    }

    void Update()
    {
        if (pumpReceivedTextureInUpdate)
            PumpReceivedVideoTexture();

        CopyMostRecentFrame();
    }

    void OnDestroy()
    {
        Disconnect();

        if (webRtcUpdateCoroutine != null)
        {
            StopCoroutine(webRtcUpdateCoroutine);
            webRtcUpdateCoroutine = null;
        }
    }

    [ContextMenu("Connect")]
    public void Connect()
    {
        if (connectCoroutine != null)
            StopCoroutine(connectCoroutine);

        connectCoroutine = StartCoroutine(ConnectRoutine());
    }

    public void SwitchCameras(string SignalUrl)
    {
        if (string.IsNullOrWhiteSpace(SignalUrl))
        {
            Debug.LogError("[WebRtcHttpReceiver] Cannot switch cameras: signaling URL is empty.");
            return;
        }

        Debug.Log($"[WebRtcHttpReceiver] Switching camera feed to {SignalUrl}");
        Disconnect();
        signalingUrl = SignalUrl;
        Connect();
    }

    [ContextMenu("Disconnect")]
    public void Disconnect()
    {
        if (connectCoroutine != null)
        {
            StopCoroutine(connectCoroutine);
            connectCoroutine = null;
        }

        ClosePeerConnection();
        ClearOutput();
    }

    void ClosePeerConnection()
    {
        if (peerConnection != null)
        {
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }
    }

    void ClearOutput()
    {
        if (frameCopyCoroutine != null)
        {
            StopCoroutine(frameCopyCoroutine);
            frameCopyCoroutine = null;
        }

        if (statsCoroutine != null)
        {
            StopCoroutine(statsCoroutine);
            statsCoroutine = null;
        }

        receivedTexture = null;
        receivedVideoTrack = null;
        lastAppliedTexture = null;
        loggedFirstFrame = false;

        if (targetRawImage != null)
            targetRawImage.texture = null;

        if (targetRenderer != null)
        {
            targetRenderer.SetPropertyBlock(null);
            targetMaterialInstance = null;
        }
    }

    IEnumerator ConnectRoutine()
    {
        ClosePeerConnection();
        ClearOutput();

        var configuration = default(RTCConfiguration);
        if (useGoogleStun)
        {
            configuration.iceServers = new[]
            {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
            };
        }

        peerConnection = new RTCPeerConnection(ref configuration);
        peerConnection.OnConnectionStateChange = state => Debug.Log($"[WebRtcHttpReceiver] Connection: {state}");
        peerConnection.OnIceConnectionChange = state => Debug.Log($"[WebRtcHttpReceiver] ICE: {state}");
        peerConnection.OnTrack = OnTrack;

        var videoTransceiver = peerConnection.AddTransceiver(
            TrackKind.Video,
            new RTCRtpTransceiverInit { direction = RTCRtpTransceiverDirection.RecvOnly });

        if (preferVp8)
            PreferCodec(videoTransceiver, "video/VP8");

        var offerOp = peerConnection.CreateOffer();
        yield return offerOp;

        if (offerOp.IsError)
        {
            FailConnect($"CreateOffer failed: {offerOp.Error.message}");
            yield break;
        }

        var offer = offerOp.Desc;
        var setLocalOp = peerConnection.SetLocalDescription(ref offer);
        yield return setLocalOp;

        if (setLocalOp.IsError)
        {
            FailConnect($"SetLocalDescription failed: {setLocalOp.Error.message}");
            yield break;
        }

        var localDescription = peerConnection.LocalDescription;
        if (waitForIceGatheringComplete)
            yield return WaitForIceGatheringComplete();

        localDescription = SafeGetCurrentLocalDescription(localDescription);

        using var request = CreateOfferRequest(signalingUrl, localDescription.sdp);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            FailConnect($"Signaling request failed: {request.responseCode} {request.error}\n{request.downloadHandler.text}");
            yield break;
        }

        var answerSdp = ExtractAnswerSdp(request.downloadHandler.text);
        if (string.IsNullOrWhiteSpace(answerSdp))
        {
            FailConnect("Signaling response did not contain an SDP answer.");
            yield break;
        }

        answerSdp = SanitizeAnswerSdp(answerSdp);

        Debug.Log(
            $"[WebRtcHttpReceiver] Signaling response {request.responseCode}, Content-Type: {request.GetResponseHeader("Content-Type")}\n" +
            FormatSdpForLog(answerSdp, 40));

        if (!answerSdp.TrimStart().StartsWith("v=0", StringComparison.Ordinal))
        {
            FailConnect("Signaling response is not SDP. It should start with 'v=0'.");
            yield break;
        }

        var answer = new RTCSessionDescription
        {
            type = RTCSdpType.Answer,
            sdp = answerSdp
        };

        RTCSetSessionDescriptionAsyncOperation setRemoteOp;
        try
        {
            setRemoteOp = peerConnection.SetRemoteDescription(ref answer);
        }
        catch (RTCErrorException ex)
        {
            FailConnect($"SetRemoteDescription threw RTCErrorException: {ex.Message}\n{FormatSdpForLog(answerSdp, 120)}");
            yield break;
        }

        yield return setRemoteOp;

        if (setRemoteOp.IsError)
        {
            FailConnect($"SetRemoteDescription failed: {setRemoteOp.Error.message}");
            yield break;
        }

        Debug.Log("[WebRtcHttpReceiver] Remote SDP applied.");
        if (logInboundStats && statsCoroutine == null)
            statsCoroutine = StartCoroutine(LogInboundStatsRoutine());

        connectCoroutine = null;
    }

    void FailConnect(string message)
    {
        Debug.LogError($"[WebRtcHttpReceiver] {message}");
        connectCoroutine = null;
    }

    void OnTrack(RTCTrackEvent trackEvent)
    {
        if (trackEvent.Track is not VideoStreamTrack videoTrack)
            return;

        receivedVideoTrack = videoTrack;
        videoTrack.OnVideoReceived += texture =>
        {
            receivedTexture = texture;
            CopyMostRecentFrame();

            if (frameCopyCoroutine == null)
            {
                frameCopyCoroutine = StartCoroutine(CopyFramesToOutputRoutine());
                Debug.Log("[WebRtcHttpReceiver] Started continuous video frame copy.");
            }

            if (!loggedFirstFrame)
            {
                loggedFirstFrame = true;
                Debug.Log($"[WebRtcHttpReceiver] First video frame received: {texture.width}x{texture.height}, {texture.GetType().Name}");
            }
        };
    }

    void PumpReceivedVideoTexture()
    {
        if (receivedVideoTrack == null)
            return;

        WebRtcReflection.UpdateTexture(receivedVideoTrack);
    }

    IEnumerator CopyFramesToOutputRoutine()
    {
        while (receivedTexture != null)
        {
            yield return null;
            CopyMostRecentFrame();
        }

        frameCopyCoroutine = null;
    }

    IEnumerator LogInboundStatsRoutine()
    {
        var wait = new WaitForSeconds(2f);

        while (peerConnection != null)
        {
            yield return wait;

            if (peerConnection == null)
                yield break;

            var op = peerConnection.GetStats();
            yield return op;

            if (op.IsError)
            {
                Debug.LogWarning($"[WebRtcHttpReceiver] GetStats failed: {op.Error.message}");
                continue;
            }

            using var report = op.Value;
            foreach (var pair in report.Stats)
            {
                if (pair.Value is RTCInboundRTPStreamStats inbound && inbound.kind == "video")
                {
                    Debug.Log(
                        "[WebRtcHttpReceiver] Inbound video stats: " +
                        $"framesReceived={inbound.framesReceived}, framesDecoded={inbound.framesDecoded}, " +
                        $"packetsReceived={inbound.packetsReceived}, bytesReceived={inbound.bytesReceived}");
                }
            }
        }
    }

    void CopyMostRecentFrame()
    {
        if (receivedTexture == null)
            return;

        if (renderReceivedTextureDirectly)
        {
            ApplyTexture(receivedTexture);
            LogHeartbeat();
            return;
        }

        EnsureOutputTexture(receivedTexture.width, receivedTexture.height);
        Graphics.Blit(receivedTexture, outputTexture);
        ApplyOutputTexture();
        LogHeartbeat();
    }

    void EnsureOutputTexture(int width, int height)
    {
        if (outputTexture != null && outputTexture.width == width && outputTexture.height == height)
            return;

        if (outputTexture != null && ownsOutputTexture)
            outputTexture.Release();

        outputTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "WebRTC Receiver Output"
        };
        outputTexture.Create();
        ownsOutputTexture = true;
    }

    void ApplyOutputTexture()
    {
        if (outputTexture == null)
            return;

        ApplyTexture(outputTexture);
    }

    void ApplyTexture(Texture texture)
    {
        if (texture == null)
            return;

        var force = reapplyDisplayTextureEveryFrame;
        if (!force && lastAppliedTexture == texture)
            return;

        if (targetRawImage != null)
            targetRawImage.texture = texture;

        if (targetRenderer != null)
            ApplyTextureToRenderer(texture, force);

        if (lastAppliedTexture != texture)
        {
            lastAppliedTexture = texture;
            Debug.Log($"[WebRtcHttpReceiver] Applied display texture: {texture.width}x{texture.height}, {texture.GetType().Name}");
        }
    }

    void LogHeartbeat()
    {
        if (!logFrameCopyHeartbeat || Time.realtimeSinceStartup < nextHeartbeatTime)
            return;

        nextHeartbeatTime = Time.realtimeSinceStartup + 2f;
        Debug.Log($"[WebRtcHttpReceiver] Rendering WebRTC texture: {receivedTexture.width}x{receivedTexture.height}, source={receivedTexture.GetType().Name}, output={(renderReceivedTextureDirectly ? "direct" : "RenderTexture")}");
    }

    void ApplyTextureToRenderer(Texture texture, bool propertyBlockOnly)
    {
        propertyBlock ??= new MaterialPropertyBlock();
        targetRenderer.GetPropertyBlock(propertyBlock);

        if (!string.IsNullOrEmpty(targetTextureProperty))
            propertyBlock.SetTexture(targetTextureProperty, texture);

        if (applyCommonTextureProperties)
        {
            propertyBlock.SetTexture("_BaseMap", texture);
            propertyBlock.SetTexture("_MainTex", texture);
            propertyBlock.SetColor("_BaseColor", Color.white);
            propertyBlock.SetColor("_Color", Color.white);
            propertyBlock.SetColor("_EmissionColor", Color.white);
        }

        targetRenderer.SetPropertyBlock(propertyBlock);

        if (propertyBlockOnly && targetMaterialInstance != null)
            return;

        targetMaterialInstance = targetRenderer.material;
        ApplyTextureToMaterial(targetMaterialInstance, texture);
    }

    void ApplyTextureToMaterial(Material material, Texture texture)
    {
        if (!string.IsNullOrEmpty(targetTextureProperty) && material.HasProperty(targetTextureProperty))
            material.SetTexture(targetTextureProperty, texture);

        if (applyCommonTextureProperties)
        {
            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", texture);

            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", texture);

            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", Color.white);

            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.white);

            if (material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", Color.white);

            material.EnableKeyword("_EMISSION");
        }

        if (material.mainTexture != texture)
            material.mainTexture = texture;
    }

    void PreferCodec(RTCRtpTransceiver transceiver, string mimeType)
    {
        var capabilities = RTCRtpSender.GetCapabilities(TrackKind.Video);
        if (capabilities?.codecs == null)
            return;

        var codecs = Array.FindAll(
            capabilities.codecs,
            codec => string.Equals(codec.mimeType, mimeType, StringComparison.OrdinalIgnoreCase));

        if (codecs.Length == 0)
        {
            Debug.LogWarning($"[WebRtcHttpReceiver] Codec not available: {mimeType}");
            return;
        }

        var error = transceiver.SetCodecPreferences(codecs);
        if (error != RTCErrorType.None)
            Debug.LogWarning($"[WebRtcHttpReceiver] SetCodecPreferences failed: {error}");
    }

    IEnumerator WaitForIceGatheringComplete()
    {
        var deadline = Time.realtimeSinceStartup + iceGatheringTimeoutSeconds;
        while (peerConnection != null &&
               peerConnection.GatheringState != RTCIceGatheringState.Complete &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        Debug.Log($"[WebRtcHttpReceiver] ICE gathering state: {peerConnection?.GatheringState}");
    }

    RTCSessionDescription SafeGetCurrentLocalDescription(RTCSessionDescription fallback)
    {
        try
        {
            return peerConnection.CurrentLocalDescription;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    UnityWebRequest CreateOfferRequest(string url, string offerSdp)
    {
        var payload = sendOfferAsJson
            ? $"{{\"type\":\"offer\",\"sdp\":\"{EscapeJsonString(offerSdp)}\"}}"
            : offerSdp;
        var body = Encoding.UTF8.GetBytes(payload);
        var request = new UnityWebRequest(url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(body),
            downloadHandler = new DownloadHandlerBuffer()
        };

        request.SetRequestHeader("Content-Type", sendOfferAsJson ? "application/json" : "application/sdp");
        request.SetRequestHeader("Accept", "application/sdp, application/json");

        if (acceptSelfSignedCertificates)
            request.certificateHandler = new AcceptAllCertificates();

        return request;
    }

    static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    static string ExtractAnswerSdp(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        var trimmed = responseBody.Trim();
        if (trimmed.StartsWith("v=0", StringComparison.Ordinal))
            return trimmed;

        var sdpValue = ExtractJsonStringValue(trimmed, "sdp");
        return string.IsNullOrWhiteSpace(sdpValue) ? trimmed : sdpValue;
    }

    static string SanitizeAnswerSdp(string sdp)
    {
        var normalized = sdp.Trim().Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var builder = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("a=fingerprint:sha-256 ", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[WebRtcHttpReceiver] Removed unsupported SDP fingerprint line: {line}");
                continue;
            }

            builder.Append(line);
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    static string FormatSdpForLog(string sdp, int maxLines)
    {
        if (string.IsNullOrEmpty(sdp))
            return "<empty>";

        var normalized = sdp.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var builder = new StringBuilder();
        var count = Mathf.Min(maxLines, lines.Length);

        for (var i = 0; i < count; i++)
            builder.AppendLine($"{i + 1:000}: {lines[i]}");

        if (lines.Length > maxLines)
            builder.AppendLine($"... {lines.Length - maxLines} more lines");

        return builder.ToString();
    }

    static string ExtractJsonStringValue(string json, string key)
    {
        var keyToken = $"\"{key}\"";
        var keyIndex = json.IndexOf(keyToken, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
            return null;

        var colonIndex = json.IndexOf(':', keyIndex + keyToken.Length);
        if (colonIndex < 0)
            return null;

        var firstQuote = json.IndexOf('"', colonIndex + 1);
        if (firstQuote < 0)
            return null;

        var builder = new StringBuilder();
        var escaping = false;

        for (var i = firstQuote + 1; i < json.Length; i++)
        {
            var c = json[i];
            if (escaping)
            {
                builder.Append(c switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => c
                });
                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (c == '"')
                return builder.ToString();

            builder.Append(c);
        }

        return null;
    }

    sealed class AcceptAllCertificates : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }

    static class WebRtcReflection
    {
        static readonly MethodInfo updateTextureMethod = typeof(VideoStreamTrack).GetMethod(
            "UpdateTexture",
            BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo flushMethod = typeof(WebRTC).Assembly
            .GetType("Unity.WebRTC.VideoUpdateMethods")
            ?.GetMethod("Flush", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        public static void UpdateTexture(VideoStreamTrack track)
        {
            if (track == null)
                return;

            updateTextureMethod?.Invoke(track, null);
            flushMethod?.Invoke(null, null);
        }
    }
}
