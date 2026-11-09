using UnityEngine;

// You always stand at the centre of the world. The rocks flow past you.
// Stand on rocks, jump the water gaps. Water or the side of a rock = game over.
// There is no physics body here: gravity and jumping are done by hand so the dotted arc can predict them exactly.
public class PinchJumpVR : MonoBehaviour
{
    [Header("Force input (0 to 1) - the Arduino will set this later")]
    [Range(0f, 1f)] public float force;
    public bool useKeyboardFake = true;   // untick when the real sensor is connected
    public float fakeStartForce = 0.2f;   // force the instant Space goes down, so quick taps still count
    public float fakeRiseSpeed = 2.5f;    // how fast fake force grows while holding Space

    [Header("Press detection (hysteresis)")]
    public float pressThreshold = 0.10f;
    public float releaseThreshold = 0.05f;

    [Header("Jump height in metres (how high your FEET go)")]
    public float minJumpHeight = 0.5f;
    public float maxJumpHeight = 1.75f;

    [Header("Forgiveness")]
    public float jumpBufferTime = 0.15f;  // release just before landing and the jump still happens
    public float coyoteTime = 0.10f;      // you can still jump this long after running off a rock edge
    public float stepTolerance = 0.15f;   // a rock edge up to this much higher than your feet is not a collision
    public float bodyRadius = 0.25f;      // how wide you are

    [Header("Camera comfort")]
    public Transform cameraRig;                          // the CameraRig (or the XR Origin)
    [Range(0f, 1f)] public float cameraFollow = 0.4f;    // 0 = view does not rise when you jump (most comfortable), 1 = full jump height

    // state
    float y;                 // height of your feet above the water
    float vy;                // vertical speed
    bool grounded;
    float coyote;
    float refY;              // height of the rock you last stood on
    float smoothRef;

    bool pressing;
    bool requireRelease;     // a press that started the run must be let go before it can count as a jump
    float peakForce;
    float lastJumpHeight;
    float bufferTimer;
    float bufferedPeak;

    // Sensor-driven mode: FsrPeakInput decides what counts as a press and which force the jump uses
    [System.NonSerialized] public bool externalPress;

    // The sensor input script (FsrUdpInput or FsrPeakInput) sets this so every jump lands in its events log
    public static System.Action<string, float> JumpLogger;
    bool extPressed;
    bool extReleaseEvent;
    float extReleaseForce;

    // dotted arc
    const int MaxArcDots = 50;
    GameObject[] dots;
    GameObject marker;
    Material dotMat;
    Material goodMat;
    Material badMat;

    void Awake()
    {
        // This object used to have a physics body. It is not needed any more: switch it off if it is still there.
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }

    void Start()
    {
        BuildArc();
        ResetPlayer();
    }

    // The Arduino/serial script will call this later
    public void SetForce(float newForce)
    {
        force = Mathf.Clamp01(newForce);
    }

    // FsrPeakInput calls this every frame: is a (filtered, confirmed) press happening, and how hard (0..1)
    public void SensorFrame(bool pressed, float liveForce)
    {
        extPressed = pressed;
        force = Mathf.Clamp01(liveForce);
    }

    // FsrPeakInput calls this once when a press ends, with the force the jump should use (0..1)
    public void SensorRelease(float jumpForce)
    {
        extReleaseEvent = true;
        extReleaseForce = Mathf.Clamp01(jumpForce);
    }

    // Called by GameManagerVR on every reset / new run
    public void ResetPlayer()
    {
        GameManagerVR gm = GameManagerVR.Instance;
        y = gm != null ? gm.startTop : 0.8f;
        vy = 0f;
        grounded = true;
        coyote = 0f;
        refY = y;
        smoothRef = y;

        // If the button/sensor that started the run is still held, ignore it until it is let go
        requireRelease = externalPress ? extPressed : force > releaseThreshold;
        extReleaseEvent = false;
        pressing = false;
        peakForce = 0f;
        bufferTimer = 0f;
        lastJumpHeight = 0f;
        HideArc();
    }

    // ---------- Input ----------

    void Update()
    {
        GameManagerVR gm = GameManagerVR.Instance;
        if (gm == null) return;

        if (useKeyboardFake)
        {
            if (Input.GetKeyDown(KeyCode.Space)) force = Mathf.Max(force, fakeStartForce);

            if (Input.GetKey(KeyCode.Space))
                force = Mathf.Min(force + fakeRiseSpeed * Time.deltaTime, 1f);
            else
                force = 0f;   // a real sensor drops almost instantly when you let go
        }

        if (!gm.IsPlaying)
        {
            HideArc();
            pressing = false;
            extReleaseEvent = false;
            bool squeezed = externalPress ? extPressed : force > pressThreshold;
            if (squeezed) gm.RequestPlayFromSensor();   // squeeze = Play / Play again
            return;
        }

        DetectPress();

        // A jump was requested: do it as soon as we are standing on a rock (within the buffer time)
        if (bufferTimer > 0f)
        {
            bufferTimer -= Time.deltaTime;
            if (grounded || coyote > 0f)
            {
                DoJump(bufferedPeak);
                bufferTimer = 0f;
            }
        }

        UpdateArc();
    }

    void DetectPress()
    {
        if (externalPress)
        {
            // The press / release decisions come from the live filter, not from the thresholds below
            if (requireRelease)
            {
                if (!extPressed) requireRelease = false;   // the press that started the run has ended
                extReleaseEvent = false;
                pressing = false;
                return;
            }

            if (!pressing && (extPressed || extReleaseEvent))
            {
                pressing = true;
                peakForce = force;
            }

            if (pressing)
            {
                peakForce = Mathf.Max(peakForce, force);

                if (extReleaseEvent)
                {
                    pressing = false;
                    bufferedPeak = extReleaseForce;
                    bufferTimer = jumpBufferTime;
                }
                else if (!extPressed)
                {
                    pressing = false;   // the press was cancelled
                }
            }
            extReleaseEvent = false;
            return;
        }

        if (requireRelease)
        {
            if (force < releaseThreshold) requireRelease = false;
            return;
        }

        if (!pressing)
        {
            if (force > pressThreshold)
            {
                pressing = true;
                peakForce = force;
            }
        }
        else
        {
            peakForce = Mathf.Max(peakForce, force);

            if (force < releaseThreshold)   // press finished -> jump using the strongest force reached
            {
                pressing = false;
                bufferedPeak = peakForce;
                bufferTimer = jumpBufferTime;
            }
        }
    }

    // Force (0 to 1) -> jump height in metres
    float HeightFromForce(float f)
    {
        // With the real sensor, 0 already means "just crossed the start threshold" and 1 means "hardest pinch",
        // so the whole range is used. The keyboard fake keeps its old dead zone below pressThreshold.
        float t = externalPress ? Mathf.Clamp01(f) : Mathf.InverseLerp(pressThreshold, 1f, f);
        return Mathf.Lerp(minJumpHeight, maxJumpHeight, t);
    }

    void DoJump(float peak)
    {
        float height = HeightFromForce(peak);
        lastJumpHeight = height;

        if (JumpLogger != null) JumpLogger("JUMP", height);              // so response time can be measured in the events file
        vy = Mathf.Sqrt(2f * GameManagerVR.Instance.gravity * height);   // v = sqrt(2*g*h)
        grounded = false;
        coyote = 0f;
    }

    // ---------- Movement (runs after the world has moved this frame) ----------

    void LateUpdate()
    {
        GameManagerVR gm = GameManagerVR.Instance;
        if (gm == null) return;

        if (gm.IsPlaying) Step(gm, Time.deltaTime);
        UpdateCamera(Time.deltaTime);
    }

    void Step(GameManagerVR gm, float dt)
    {
        Vector3 dir = gm.heading;

        // What is under you right now. You are always at the origin; the rocks move.
        GameManagerVR.Probe now = gm.ProbeSurface(Vector3.zero, dir, bodyRadius);

        if (grounded)
        {
            // Also look at the places you passed through since last frame, so a low frame rate can never skip over a rock side
            GameManagerVR.Probe worst = now;
            float moved = gm.speed * dt;
            int n = Mathf.CeilToInt(moved / 0.2f);
            for (int i = 1; i <= n; i++)
            {
                GameManagerVR.Probe p = gm.ProbeSurface(-dir * (moved * i / n), dir, bodyRadius);
                if (p.top > worst.top) worst = p;
            }

            if (worst.top > y + stepTolerance)
            {
                gm.GameOver(worst.stack ? "You ran into the side of a stacked rock" : "You ran into the side of a rock");
                return;
            }

            if (now.top >= y - 0.001f)
            {
                y = Mathf.Max(y, now.top);   // small steps up are fine
            }
            else
            {
                grounded = false;            // the rock ended under your feet: start falling
                vy = 0f;
                coyote = coyoteTime;
            }
        }

        if (!grounded)
        {
            float prevY = y;
            vy -= gm.gravity * dt;
            y += vy * dt;
            if (coyote > 0f) coyote -= dt;

            if (now.top > y)    // your feet are below a rock surface
            {
                if (vy <= 0f && prevY >= now.top - stepTolerance)
                {
                    y = now.top;             // landed on top of it
                    vy = 0f;
                    grounded = true;
                    coyote = 0f;
                    gm.NotifyLanding(now.id, now.stack);
                }
                else
                {
                    gm.GameOver(now.stack ? "You hit the side of a stacked rock" : "You hit the side of a rock");
                    return;
                }
            }
            else if (y <= gm.waterLevel && now.top <= gm.waterLevel)
            {
                gm.GameOver("You fell in the water");
                return;
            }
        }

        if (grounded) refY = y;
    }

    void UpdateCamera(float dt)
    {
        if (cameraRig == null) return;

        // Follow the rock you stand on smoothly, but only a fraction of the jump itself (comfort)
        smoothRef = Mathf.Lerp(smoothRef, refY, 1f - Mathf.Exp(-dt / 0.2f));
        float rigY = smoothRef + cameraFollow * (y - refY);
        cameraRig.position = new Vector3(0f, rigY, 0f);
    }

    // ---------- Dotted arc: where the jump you are charging will take you ----------

    void BuildArc()
    {
        Material baseMat = GameManagerVR.GetBaseMaterial();
        dotMat = new Material(baseMat);
        dotMat.color = new Color(1f, 0.92f, 0.2f);
        goodMat = new Material(baseMat);
        goodMat.color = new Color(0.2f, 0.9f, 0.3f);
        badMat = new Material(baseMat);
        badMat.color = new Color(0.95f, 0.2f, 0.2f);

        dots = new GameObject[MaxArcDots];
        for (int i = 0; i < MaxArcDots; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            d.name = "ArcDot";
            Destroy(d.GetComponent<Collider>());
            d.transform.localScale = Vector3.one * 0.09f;
            d.GetComponent<Renderer>().sharedMaterial = dotMat;
            d.SetActive(false);
            dots[i] = d;
        }

        marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = "ArcLanding";
        Destroy(marker.GetComponent<Collider>());
        marker.transform.localScale = new Vector3(0.7f, 0.02f, 0.7f);
        marker.SetActive(false);
    }

    void HideArc()
    {
        if (dots == null) return;
        for (int i = 0; i < dots.Length; i++) dots[i].SetActive(false);
        marker.SetActive(false);
    }

    void UpdateArc()
    {
        GameManagerVR gm = GameManagerVR.Instance;
        bool show = pressing && (grounded || coyote > 0f);
        if (!show)
        {
            HideArc();
            return;
        }

        float g = gm.gravity;
        float v0 = Mathf.Sqrt(2f * g * HeightFromForce(peakForce));
        Vector3 dir = gm.heading;
        float dt = 0.04f;

        float prevY = y;
        int used = 0;
        int outcome = 0;                 // 0 = still in the air, 1 = lands on a rock, 2 = water, 3 = hits a rock side
        Vector3 endPos = Vector3.zero;

        for (int i = 1; i < MaxArcDots; i++)
        {
            float t = i * dt;
            float yy = y + v0 * t - 0.5f * g * t * t;
            float vv = v0 - g * t;
            Vector3 p = dir * (gm.speed * t);        // you keep moving forward at the world speed
            float top = gm.SurfaceHeight(p, dir, bodyRadius);

            if (top > yy)
            {
                if (vv <= 0f && prevY >= top - stepTolerance)
                {
                    outcome = 1;
                    endPos = new Vector3(p.x, top, p.z);
                }
                else
                {
                    outcome = 3;
                    endPos = new Vector3(p.x, yy, p.z);
                }
                break;
            }

            if (yy <= gm.waterLevel && top <= gm.waterLevel)
            {
                outcome = 2;
                endPos = new Vector3(p.x, gm.waterLevel, p.z);
                break;
            }

            dots[used].transform.position = new Vector3(p.x, yy, p.z);
            dots[used].SetActive(true);
            used++;
            prevY = yy;
        }

        for (int i = used; i < MaxArcDots; i++) dots[i].SetActive(false);

        if (outcome != 0)
        {
            marker.transform.position = endPos;
            marker.GetComponent<Renderer>().sharedMaterial = (outcome == 1) ? goodMat : badMat;
            marker.SetActive(true);
        }
        else
        {
            marker.SetActive(false);
        }
    }

    // ---------- Charge bar (desktop only - OnGUI does not show inside a headset) ----------

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (GameManagerVR.Instance == null || !GameManagerVR.Instance.IsPlaying) return;

        DrawChargeBar();
    }

    void DrawRect(Rect r, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    Color ChargeColor(float f)
    {
        return f < 0.5f ? Color.Lerp(Color.green, Color.yellow, f * 2f)
                        : Color.Lerp(Color.yellow, Color.red, (f - 0.5f) * 2f);
    }

    void DrawChargeBar()
    {
        float w = 300f, h = 22f;
        float x = 10f, yPos = Screen.height - h - 20f;

        DrawRect(new Rect(x - 2, yPos - 2, w + 4, h + 4), new Color(0f, 0f, 0f, 0.6f));                  // background
        DrawRect(new Rect(x, yPos, w * Mathf.Clamp01(force), h), ChargeColor(force));                    // fill
        DrawRect(new Rect(x + w * pressThreshold - 1, yPos - 4, 2, h + 8), Color.white);                 // white tick = minimum force to count as a press
        if (pressing)
            DrawRect(new Rect(x + w * Mathf.Clamp01(peakForce) - 1, yPos - 4, 3, h + 8), Color.yellow);  // yellow tick = peak force the jump will use

        string label = "Charge " + force.ToString("F2");
        if (pressing) label += "   jump height " + HeightFromForce(peakForce).ToString("F2") + " m";
        GUI.Label(new Rect(x, yPos - 28, 400, 22), label);
    }
}
