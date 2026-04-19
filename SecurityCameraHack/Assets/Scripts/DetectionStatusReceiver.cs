using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class DetectionStatusReceiver : MonoBehaviour
{
    [SerializeField] string detectionUrl = "https://10.10.9.81:8080/detection";
    [SerializeField] bool acceptSelfSignedCertificates = true;
    [SerializeField] bool connectWhenEnabled;
    [SerializeField] bool streamOnlyWhenBothCamerasInactive = true;
    [SerializeField] bool logLifecycle = true;
    [SerializeField] float reconnectDelaySeconds = 1f;

    public MeshRenderer[] cams;
    public Material red;
    public Material green;

    public bool Bit0 { get; private set; }
    public bool Bit1 { get; private set; }
    public bool IsConnected => request != null && request.result == UnityWebRequest.Result.InProgress;

    Coroutine streamCoroutine;
    UnityWebRequest request;
    bool shouldStream;
    bool camera0Active;
    bool camera1Active;

    void OnEnable()
    {
        if (logLifecycle)
            Debug.Log($"[DetectionStatusReceiver] Enabled. connectWhenEnabled={connectWhenEnabled}, streamOnlyWhenBothCamerasInactive={streamOnlyWhenBothCamerasInactive}");
    }

    void Start()
    {
        if (connectWhenEnabled)
        {
            EnterDetectionMode();
        }
        else if (logLifecycle)
        {
            Debug.Log("[DetectionStatusReceiver] Idle. Check Connect When Enabled or call EnterDetectionMode().");
        }
    }

    void OnDisable()
    {
        StopDetectionStream();
    }

    private void Update()
    {
        if (cams == null)
            return;

        for (int i = 0; i < cams.Length; i++)
        {
            if (cams[i] == null)
                continue;

            if (i < cams.Length / 2)
            {
                if (Bit0) cams[i].material = red;
                else cams[i].material = green;
            }
            else
            {
                if (Bit1) cams[i].material = red;
                else cams[i].material = green;
            }
        }
    }

    public void SetCamerasActive(bool camera0, bool camera1)
    {
        camera0Active = camera0;
        camera1Active = camera1;

        if (logLifecycle)
            Debug.Log($"[DetectionStatusReceiver] SetCamerasActive camera0={camera0Active}, camera1={camera1Active}");

        if (!streamOnlyWhenBothCamerasInactive || (!camera0Active && !camera1Active))
            StartDetectionStream();
        else
            StopDetectionStream();
    }

    [ContextMenu("Enter Detection Mode")]
    public void EnterDetectionMode()
    {
        SetCamerasActive(false, false);
    }

    [ContextMenu("Exit Detection Mode")]
    public void ExitDetectionMode()
    {
        StopDetectionStream();
    }

    void StartDetectionStream()
    {
        shouldStream = true;

        if (streamCoroutine == null)
        {
            if (logLifecycle)
                Debug.Log("[DetectionStatusReceiver] Starting detection stream.");

            streamCoroutine = StartCoroutine(StreamRoutine());
        }
        else if (logLifecycle)
        {
            Debug.Log("[DetectionStatusReceiver] Detection stream is already running.");
        }
    }

    void StopDetectionStream()
    {
        if (logLifecycle && (shouldStream || streamCoroutine != null || request != null))
            Debug.Log("[DetectionStatusReceiver] Stopping detection stream.");

        shouldStream = false;

        if (streamCoroutine != null)
        {
            StopCoroutine(streamCoroutine);
            streamCoroutine = null;
        }

        if (request != null)
        {
            request.Abort();
            request.Dispose();
            request = null;
        }
    }

    IEnumerator StreamRoutine()
    {
        while (shouldStream)
        {
            request = new UnityWebRequest(detectionUrl, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DetectionDownloadHandler(HandleLine)
            };

            request.SetRequestHeader("Accept", "text/event-stream, text/plain");

            if (acceptSelfSignedCertificates)
                request.certificateHandler = new AcceptAllCertificates();

            Debug.Log($"[DetectionStatusReceiver] Connecting to {detectionUrl}");
            yield return request.SendWebRequest();

            if (shouldStream && request.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[DetectionStatusReceiver] Stream disconnected: {request.responseCode} {request.error}");

            request.Dispose();
            request = null;

            if (shouldStream)
                yield return new WaitForSeconds(reconnectDelaySeconds);
        }

        streamCoroutine = null;
    }

    void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var payload = line.Trim();
        Debug.Log("PAYLOAD: " + payload);
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            payload = payload.Substring(5).Trim();

        if (!TryParseBits(payload, out var bit0, out var bit1))
            return;

        Bit0 = bit0;
        Bit1 = bit1;

        Debug.Log($"[DetectionStatusReceiver] Bits: {Bit0} {Bit1}");
    }

    static bool TryParseBits(string payload, out bool bit0, out bool bit1)
    {
        bit0 = false;
        bit1 = false;

        var parts = payload.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            bit0 = ParseBit(parts[0]);
            bit1 = ParseBit(parts[1]);
            return true;
        }

        if (parts.Length == 1 && parts[0].Length >= 2)
        {
            bit0 = ParseBit(parts[0][0].ToString());
            bit1 = ParseBit(parts[0][1].ToString());
            return true;
        }

        return false;
    }

    static bool ParseBit(string value)
    {
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    sealed class DetectionDownloadHandler : DownloadHandlerScript
    {
        readonly Action<string> onLine;
        readonly StringBuilder lineBuffer = new StringBuilder();

        public DetectionDownloadHandler(Action<string> onLine) : base(new byte[1024])
        {
            this.onLine = onLine;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0)
                return true;

            var chunk = Encoding.UTF8.GetString(data, 0, dataLength);
            foreach (var character in chunk)
            {
                if (character == '\n')
                {
                    var line = lineBuffer.ToString().TrimEnd('\r');
                    lineBuffer.Clear();
                    onLine?.Invoke(line);
                    continue;
                }

                lineBuffer.Append(character);
            }

            return true;
        }
    }

    sealed class AcceptAllCertificates : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }
}
