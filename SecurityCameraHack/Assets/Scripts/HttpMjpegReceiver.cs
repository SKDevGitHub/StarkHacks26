using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public sealed class HttpMjpegReceiver : MonoBehaviour
{
    [Header("Stream")]
    [SerializeField] string streamUrl = "https://10.10.9.81:8080";
    [SerializeField] bool autoConnect = true;
    [SerializeField] bool acceptSelfSignedCertificates = true;

    [Header("Output")]
    [SerializeField] RawImage targetRawImage;
    [SerializeField] Renderer targetRenderer;
    [SerializeField] string targetTextureProperty = "_BaseMap";
    [SerializeField] RenderTexture outputTexture;

    UnityWebRequest request;
    Texture2D frameTexture;
    Coroutine connectCoroutine;
    bool ownsOutputTexture;

    public RenderTexture OutputTexture => outputTexture;
    public Texture2D FrameTexture => frameTexture;

    void Start()
    {
        if (autoConnect)
            Connect();
    }

    void OnDestroy()
    {
        Disconnect();
    }

    [ContextMenu("Connect")]
    public void Connect()
    {
        Disconnect();
        connectCoroutine = StartCoroutine(ConnectRoutine());
    }

    [ContextMenu("Disconnect")]
    public void Disconnect()
    {
        if (connectCoroutine != null)
        {
            StopCoroutine(connectCoroutine);
            connectCoroutine = null;
        }

        if (request != null)
        {
            request.Abort();
            request.Dispose();
            request = null;
        }
    }

    System.Collections.IEnumerator ConnectRoutine()
    {
        var handler = new JpegStreamDownloadHandler(OnJpegFrame);
        request = new UnityWebRequest(streamUrl, UnityWebRequest.kHttpVerbGET)
        {
            downloadHandler = handler
        };

        if (acceptSelfSignedCertificates)
            request.certificateHandler = new AcceptAllCertificates();

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success &&
            request.result != UnityWebRequest.Result.InProgress)
        {
            Debug.LogError($"[HttpMjpegReceiver] Stream failed: {request.responseCode} {request.error}");
        }

        connectCoroutine = null;
    }

    void OnJpegFrame(byte[] jpeg)
    {
        if (frameTexture == null)
            frameTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

        if (!frameTexture.LoadImage(jpeg, false))
        {
            Debug.LogWarning("[HttpMjpegReceiver] Failed to decode JPEG frame.");
            return;
        }

        EnsureOutputTexture(frameTexture.width, frameTexture.height);
        Graphics.Blit(frameTexture, outputTexture);

        if (targetRawImage != null)
            targetRawImage.texture = outputTexture;

        if (targetRenderer != null)
            ApplyTextureToRenderer(outputTexture);
    }

    void EnsureOutputTexture(int width, int height)
    {
        if (outputTexture != null && outputTexture.width == width && outputTexture.height == height)
            return;

        if (outputTexture != null && ownsOutputTexture)
            outputTexture.Release();

        outputTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "MJPEG Receiver Output"
        };
        outputTexture.Create();
        ownsOutputTexture = true;
    }

    void ApplyTextureToRenderer(Texture texture)
    {
        var material = targetRenderer.material;

        if (!string.IsNullOrEmpty(targetTextureProperty) && material.HasProperty(targetTextureProperty))
            material.SetTexture(targetTextureProperty, texture);
        else
            material.mainTexture = texture;
    }

    sealed class JpegStreamDownloadHandler : DownloadHandlerScript
    {
        readonly Action<byte[]> onFrame;
        readonly List<byte> buffer = new();

        public JpegStreamDownloadHandler(Action<byte[]> onFrame) : base(new byte[64 * 1024])
        {
            this.onFrame = onFrame;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0)
                return true;

            for (var i = 0; i < dataLength; i++)
                buffer.Add(data[i]);

            ExtractFrames();
            return true;
        }

        void ExtractFrames()
        {
            while (true)
            {
                var start = IndexOfMarker(0, 0xff, 0xd8);
                if (start < 0)
                {
                    TrimBuffer();
                    return;
                }

                var end = IndexOfMarker(start + 2, 0xff, 0xd9);
                if (end < 0)
                {
                    if (start > 0)
                        buffer.RemoveRange(0, start);
                    return;
                }

                var length = end + 2 - start;
                var frame = buffer.GetRange(start, length).ToArray();
                buffer.RemoveRange(0, end + 2);
                onFrame?.Invoke(frame);
            }
        }

        int IndexOfMarker(int offset, byte first, byte second)
        {
            for (var i = Mathf.Max(0, offset); i < buffer.Count - 1; i++)
            {
                if (buffer[i] == first && buffer[i + 1] == second)
                    return i;
            }

            return -1;
        }

        void TrimBuffer()
        {
            const int maxBufferedBytes = 1024 * 1024;
            if (buffer.Count > maxBufferedBytes)
                buffer.RemoveRange(0, buffer.Count - maxBufferedBytes);
        }
    }

    sealed class AcceptAllCertificates : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }
}
