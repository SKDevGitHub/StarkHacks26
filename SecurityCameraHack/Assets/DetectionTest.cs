using UnityEngine;
using System.Linq;
using UI = UnityEngine.UI;
using Klak.TestTools;
using System.Collections;
using System.Collections.Generic;
using System;
using Meta.XR;
using Unity.WebRTC;

sealed class DetectionTest : MonoBehaviour
{
    [SerializeField] ImageSource _source = null;
    [SerializeField] int _decimation = 4;
    [SerializeField] float _tagSize = 0.05f;
    [SerializeField] Material _tagMaterial = null;
    [SerializeField] UI.RawImage _webcamPreview = null;
    [SerializeField] UI.Text _debugText = null;
    [SerializeField] bool _transformPassthroughPoseToWorld = true;

    public PassthroughCameraAccess passthroughCameraAccess;
    public Transform[] tags;
    public int[] detectedIDs;
    public WebRtcHttpReceiver receiver;

    AprilTag.TagDetector _detector;
    TagDrawer _drawer;
    Vector2Int _detectorResolution;

    float elapsed = 0f;
    public float duration = 1.0f;

    private Vector3[] lastTagPosition;
    private Quaternion[] lastTagRotation;

    private bool coolingDown;

    void Start()
    {
        lastTagPosition = new Vector3[5];
        lastTagRotation = new Quaternion[5];
        _drawer = new TagDrawer(_tagMaterial);
    }

    void OnDestroy()
    {
        _detector?.Dispose();
        _drawer?.Dispose();
    }

    IEnumerator CoolDown()
    {
        coolingDown = true;
        yield return new WaitForSeconds(2f);
        coolingDown = false;
    }

    void LateUpdate()
    {
        if (!TryGetFrame(out var image, out var resolution, out var previewTexture, out var fov, out var usingPassthrough))
            return;

        if (_webcamPreview != null)
            _webcamPreview.texture = previewTexture;

        if (image.IsEmpty) return;

        EnsureDetector(resolution);

        // AprilTag detection
        _detector.ProcessImage(image, fov, _tagSize);
        var cameraPose = usingPassthrough && _transformPassthroughPoseToWorld
            ? passthroughCameraAccess.GetCameraPose()
            : default;

        int i = 0;
        var detectedCount = 0;

        elapsed += Time.deltaTime;
        float percentComplete = elapsed / duration;

        foreach (var tag in _detector.DetectedTags)
        {
            detectedCount++;

            if (tags == null || i >= tags.Length || tags[i] == null)
                continue;

            var tagPosition = tag.Position;
            var tagRotation = tag.Rotation;

            if (usingPassthrough && _transformPassthroughPoseToWorld)
            {
                tagPosition = cameraPose.position + cameraPose.rotation * tagPosition;
                tagRotation = cameraPose.rotation * tagRotation;
            }

            if (!tags[tag.ID].gameObject.activeInHierarchy)
            {
                tags[tag.ID].position = tagPosition;
                tags[tag.ID].rotation = tagRotation;
                tags[tag.ID].gameObject.SetActive(true);
            }

            tags[tag.ID].localScale = Vector3.one * _tagSize * 30;

            lastTagPosition[tag.ID] = tagPosition;
            lastTagRotation[tag.ID] = tagRotation;

            if (detectedIDs != null && i < detectedIDs.Length)
            {
                detectedIDs[i] = tag.ID;

                if (i == 0 && receiver != null && !coolingDown)
                {
                    if (tag.ID == 0) SwitchCamera("https://10.10.9.81:8080/0/whep");
                    if (tag.ID == 1) SwitchCamera("https://10.10.9.81:8080/1/whep");
                }
            }

            i++;
        }

        for (int tag_idx = 0; tag_idx < tags.Length; tag_idx ++) {
            tags[tag_idx].position = Vector3.Lerp(tags[tag_idx].position, lastTagPosition[tag_idx], percentComplete);
            tags[tag_idx].rotation = Quaternion.Slerp(tags[tag_idx].rotation, lastTagRotation[tag_idx], percentComplete);
        }


        // Profile data output (with 30 frame interval)
        if (_debugText != null && Time.frameCount % 30 == 0)
            _debugText.text = _detector.ProfileData.Aggregate
              ($"Source: {(usingPassthrough ? "Quest passthrough" : "ImageSource")}\nResolution: {resolution.x}x{resolution.y}\nFOV: {fov * Mathf.Rad2Deg:F1}\nTags: {detectedCount}\nProfile (usec)", (c, n) => $"{c}\n{n.name} : {n.time}");
    }

    bool TryGetFrame(out ReadOnlySpan<Color32> image, out Vector2Int resolution, out Texture previewTexture, out float fov, out bool usingPassthrough)
    {
        if (passthroughCameraAccess != null && passthroughCameraAccess.IsPlaying)
        {
            usingPassthrough = true;
            resolution = passthroughCameraAccess.CurrentResolution;
            previewTexture = passthroughCameraAccess.GetTexture();
            fov = GetPassthroughVerticalFov(passthroughCameraAccess);

            if (resolution.x <= 0 || resolution.y <= 0)
            {
                image = ReadOnlySpan<Color32>.Empty;
                return false;
            }

            var colors = passthroughCameraAccess.GetColors();
            image = colors.AsReadOnlySpan(resolution.x * resolution.y);
            return !image.IsEmpty;
        }

        if (_source != null && _source.AsTexture != null)
        {
            usingPassthrough = false;
            resolution = _source.OutputResolution;
            previewTexture = _source.AsTexture;
            fov = Camera.main != null ? Camera.main.fieldOfView * Mathf.Deg2Rad : Mathf.PI / 3f;
            image = _source.AsTexture.AsSpan();
            return !image.IsEmpty;
        }

        image = ReadOnlySpan<Color32>.Empty;
        resolution = default;
        previewTexture = null;
        fov = 0;
        usingPassthrough = false;
        return false;
    }

    void EnsureDetector(Vector2Int resolution)
    {
        if (_detector != null && _detectorResolution == resolution)
            return;

        _detector?.Dispose();
        _detector = new AprilTag.TagDetector(resolution.x, resolution.y, _decimation);
        _detectorResolution = resolution;
        Debug.Log($"[DetectionTest] AprilTag detector initialized for {resolution.x}x{resolution.y}");
    }

    void SwitchCamera(string signalingUrl)
    {
        StartCoroutine(CoolDown());
        receiver.SwitchCameras(signalingUrl);
    }

    static float GetPassthroughVerticalFov(PassthroughCameraAccess access)
    {
        var resolution = access.CurrentResolution;
        var focalY = access.Intrinsics.FocalLength.y;

        if (resolution.y > 0 && focalY > 0)
            return 2f * Mathf.Atan(resolution.y / (2f * focalY));

        return Camera.main != null ? Camera.main.fieldOfView * Mathf.Deg2Rad : Mathf.PI / 3f;
    }
}
