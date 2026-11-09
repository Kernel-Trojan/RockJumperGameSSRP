using UnityEngine;

// FOR TESTING ON A MONITOR ONLY.
// Put this on the Main Camera. Hold the RIGHT mouse button and move the mouse to look around.
// Do NOT use it on the VR camera - the headset does the looking there.
public class DesktopLookVR : MonoBehaviour
{
    public float sensitivity = 2f;

    float yaw;
    float pitch;

    void Update()
    {
        if (!Input.GetMouseButton(1)) return;

        yaw += Input.GetAxis("Mouse X") * sensitivity;
        pitch -= Input.GetAxis("Mouse Y") * sensitivity;
        pitch = Mathf.Clamp(pitch, -80f, 80f);

        transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
    }
}
