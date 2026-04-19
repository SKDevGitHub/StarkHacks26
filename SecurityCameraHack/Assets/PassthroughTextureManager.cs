using Klak.TestTools;
using Meta.XR;
using System.Threading.Tasks;
using UnityEngine;

public class PassthroughTextureManager : MonoBehaviour
{
    public ImageSource imageSource;
    public PassthroughCameraAccess cameraAccess;

    // Update is called once per frame
    void Start()
    {
        SendTexture();
    }

    private async void SendTexture()
    {
        while (true)
        {
            imageSource.SourceTexture = cameraAccess.GetTexture2D();
            await Task.Yield();
        }
    }
}
