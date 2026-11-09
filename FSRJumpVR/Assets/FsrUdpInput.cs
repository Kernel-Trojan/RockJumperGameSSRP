using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Receives the already-filtered sensor data from fsr_bridge.py (running on the same PC) and drives the jump.
//
//   Arduino (COM5) -> fsr_bridge.py: median of 3 -> 8-tap moving average -> rest level subtracted -> press/peak/release
//                  -> UDP messages on this PC (port 5005) -> this script -> PinchJumpVR (jump height = highest value of the press)
//
// Unity does NOT open COM5 any more, so the serial port, the .NET Framework setting and the old
// FsrSerialInput / FsrPeakInput components are not needed. Remove them from the GameManager.
//
// Put this on the GameManager object and drag the Player into the Player slot.

public class FsrUdpInput : MonoBehaviour
{
    public static FsrUdpInput Instance;

    [Header("Drag in")]
    public PinchJumpVR player;

    [Header("Connection (must match fsr_bridge.py)")]
    public int listenPort = 5005;

    [Header("Jump force")]
    public float fullScaleVolts = 0.60f;        // peak ABOVE THE REST LEVEL that gives the highest jump. Press K to measure your own hardest pinch.

    [Header("Keys (on the monitor)")]
    public KeyCode fullScaleKey = KeyCode.K;
    public float fullScaleSeconds = 4f;

    [Header("Other")]
    public bool keyboardWhenDisconnected = true;   // Space bar works while fsr_bridge.py is not running
    public bool logToCsv = true;                   // writes FsrLogs/fsr_unity_events_*.csv in the project folder

    // states sent by fsr_bridge.py
    const int StIdle = 0, StPressed = 1, StReleasing = 2, StCalibrating = 3;

    class Packet
    {
        public double unix;
        public double raw, filtered, delta;
        public int state, count;
        public double lastPeak, curPeak;
    }

    // receiving thread
    UdpClient udp;
    Thread thread;
    volatile bool running;
    readonly ConcurrentQueue<Packet> queue = new ConcurrentQueue<Packet>();
    readonly object lockObj = new object();
    string status = "starting";
    System.Diagnostics.Stopwatch clock;

    // latest values (main thread only)
    bool gotData, connected;
    double lastPacketClock;
    int state;
    double delta, curPeak, lastPeakVolts;
    float lastPeakForce;
    bool haveCount;
    int lastCount;
    bool releaseReady;
    float releaseForce;
    string note = "";

    // measuring the hardest pinch
    bool measuringFull;
    float measureEnd;
    double maxDelta;

    // logging
    StreamWriter eventsW;
    float lastFlush;
    GUIStyle guiStyle;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    void OnEnable()
    {
        Instance = this;
        PinchJumpVR.JumpLogger = LogEvent;
        clock = System.Diagnostics.Stopwatch.StartNew();
        OpenLog();

        try
        {
            udp = new UdpClient(listenPort);
            udp.Client.ReceiveTimeout = 200;
            running = true;
            thread = new Thread(ReceiveLoop);
            thread.IsBackground = true;
            thread.Start();
            lock (lockObj) { status = "listening on UDP port " + listenPort; }
        }
        catch (Exception e)
        {
            lock (lockObj) { status = "cannot listen on port " + listenPort + ": " + e.Message; }
            Debug.LogWarning("FsrUdpInput: " + e.Message);
        }
    }

    void OnDisable()
    {
        running = false;
        if (thread != null) thread.Join(1000);
        thread = null;
        if (udp != null) { try { udp.Close(); } catch (Exception) { } udp = null; }

        if (eventsW != null) { eventsW.Flush(); eventsW.Close(); eventsW = null; }
        if (Instance == this) Instance = null;
        PinchJumpVR.JumpLogger = null;
    }

    // ---------- receiving thread ----------

    void ReceiveLoop()
    {
        IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] data = udp.Receive(ref any);
                Packet p = ParsePacket(Encoding.ASCII.GetString(data));
                if (p != null) queue.Enqueue(p);
            }
            catch (SocketException)
            {
                // normally just "nothing arrived for 200 ms"
                Thread.Sleep(5);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    // "S,unix,t_ms,raw,filtered,delta,state,count,last_peak,cur_peak"
    static Packet ParsePacket(string text)
    {
        string[] f = text.Trim().Split(',');
        if (f.Length < 10 || f[0] != "S") return null;

        Packet p = new Packet();
        if (!double.TryParse(f[1], NumberStyles.Float, Inv, out p.unix)) return null;
        if (!double.TryParse(f[3], NumberStyles.Float, Inv, out p.raw)) return null;
        if (!double.TryParse(f[4], NumberStyles.Float, Inv, out p.filtered)) return null;
        if (!double.TryParse(f[5], NumberStyles.Float, Inv, out p.delta)) return null;
        if (!int.TryParse(f[6], NumberStyles.Integer, Inv, out p.state)) return null;
        if (!int.TryParse(f[7], NumberStyles.Integer, Inv, out p.count)) return null;
        if (!double.TryParse(f[8], NumberStyles.Float, Inv, out p.lastPeak)) return null;
        if (!double.TryParse(f[9], NumberStyles.Float, Inv, out p.curPeak)) return null;
        return p;
    }

    // ---------- main thread ----------

    void Update()
    {
        Packet p;
        while (queue.TryDequeue(out p))
        {
            ProcessPacket(p);
            lastPacketClock = clock.Elapsed.TotalSeconds;
            gotData = true;
        }

        bool wasConnected = connected;
        connected = gotData && (clock.Elapsed.TotalSeconds - lastPacketClock) < 1.0;
        if (wasConnected && !connected)
        {
            haveCount = false;       // the bridge stopped: forget everything half-finished
            state = StIdle;
            releaseReady = false;
        }

        if (Input.GetKeyDown(fullScaleKey)) StartMeasureFull();
        FinishMeasureFull();

        if (player != null)
        {
            if (!connected)
            {
                player.externalPress = false;
                player.useKeyboardFake = keyboardWhenDisconnected;
                if (!keyboardWhenDisconnected) player.SetForce(0f);
                releaseReady = false;
            }
            else
            {
                player.useKeyboardFake = false;
                player.externalPress = true;

                bool live = !measuringFull && state != StCalibrating;
                bool pressedForPlayer = live && (state == StPressed || state == StReleasing);   // stays true while the bridge waits out a short dip
                float liveForce = 0f;
                if (pressedForPlayer)
                    liveForce = ForceFromDelta(state == StReleasing ? curPeak : delta);
                player.SensorFrame(pressedForPlayer, liveForce);

                if (releaseReady)
                {
                    if (live) player.SensorRelease(releaseForce);
                    releaseReady = false;
                }
            }
        }

        if (Time.unscaledTime - lastFlush > 1f)
        {
            lastFlush = Time.unscaledTime;
            if (eventsW != null) eventsW.Flush();
        }
    }

    void ProcessPacket(Packet p)
    {
        state = p.state;
        delta = p.delta;
        curPeak = p.curPeak;
        if (measuringFull) maxDelta = Math.Max(maxDelta, p.delta);

        if (!haveCount || p.count < lastCount)
        {
            // first packet, or the bridge was restarted: just take its counter, no jump
            lastCount = p.count;
            haveCount = true;
        }
        else if (p.count > lastCount)
        {
            lastCount = p.count;
            lastPeakVolts = p.lastPeak;
            lastPeakForce = ForceFromDelta(p.lastPeak);
            if (!measuringFull)
            {
                releaseForce = lastPeakForce;
                releaseReady = true;
            }
            WriteEvent("RELEASE_RECEIVED", p.lastPeak);
        }
    }

    float ForceFromDelta(double volts)
    {
        double f = volts / Math.Max(fullScaleVolts, 0.01f);
        if (f < 0.0) f = 0.0;
        if (f > 1.0) f = 1.0;
        return (float)f;
    }

    // ---------- measuring the hardest pinch ----------

    void StartMeasureFull()
    {
        measuringFull = true;
        measureEnd = Time.unscaledTime + fullScaleSeconds;
        maxDelta = 0.0;
        releaseReady = false;
        note = "Pinch as hard as you can for " + fullScaleSeconds.ToString("0") + " seconds...";
    }

    void FinishMeasureFull()
    {
        if (!measuringFull || Time.unscaledTime < measureEnd) return;
        measuringFull = false;

        if (maxDelta > 0.05)
        {
            fullScaleVolts = (float)maxDelta;
            note = "Hardest pinch = " + fullScaleVolts.ToString("0.000") + " V above rest. That now gives the highest jump. (Write it in the Inspector to keep it.)";
            WriteEvent("FULL_SCALE", fullScaleVolts);
        }
        else
        {
            note = "No hard pinch was seen (highest " + maxDelta.ToString("0.000") + " V). Full scale unchanged.";
        }
    }

    // ---------- logging (the signal itself is logged by fsr_bridge.py) ----------

    static double UnixNow()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    void OpenLog()
    {
        if (!logToCsv) return;
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "FsrLogs");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            eventsW = new StreamWriter(Path.Combine(dir, "fsr_unity_events_" + stamp + ".csv"));
            eventsW.WriteLine("Unix_Time_s,Event,Value");
        }
        catch (Exception e)
        {
            Debug.LogWarning("FsrUdpInput: could not create the log file: " + e.Message);
            eventsW = null;
        }
    }

    void WriteEvent(string name, double value)
    {
        if (eventsW == null) return;
        eventsW.WriteLine(UnixNow().ToString("0.000", Inv) + "," + name + "," + value.ToString("0.0000", Inv));
    }

    // Called by PinchJumpVR when it actually jumps. Same clock as fsr_bridge.py's events file (Unix time),
    // so JUMP time minus RELEASE time = delay from the bridge deciding to the player leaving the ground.
    void LogEvent(string name, float value)
    {
        WriteEvent(name, value);
    }

    // ---------- on-screen status (desktop only; a headset does not show OnGUI) ----------

    void OnGUI()
    {
        if (guiStyle == null)
        {
            guiStyle = new GUIStyle(GUI.skin.label);
            guiStyle.fontSize = 14;
            guiStyle.alignment = TextAnchor.LowerRight;
            guiStyle.wordWrap = true;
            guiStyle.normal.textColor = Color.white;
        }

        string text;
        if (connected)
        {
            string stateName = state == StPressed ? "pressed" : state == StReleasing ? "releasing" : state == StCalibrating ? "MEASURING REST - keep the finger OFF" : "idle";
            text = "FSR bridge   above rest " + (delta * 1000.0).ToString("0.0") + " mV   " + stateName +
                   "\nlast peak " + (lastPeakVolts * 1000.0).ToString("0") + " mV -> force " + lastPeakForce.ToString("0.00") +
                   "\nfull scale " + fullScaleVolts.ToString("0.000") + " V   (" + fullScaleKey + " = measure hardest pinch)";
            if (note.Length > 0) text += "\n" + note;
        }
        else
        {
            string st;
            lock (lockObj) { st = status; }
            text = "Waiting for fsr_bridge.py (" + st + ")\nStart it with:  python fsr_bridge.py COM5" +
                   (keyboardWhenDisconnected ? "\nSpace bar works instead" : "");
        }

        GUI.Label(new Rect(Screen.width - 620, Screen.height - 120, 610, 110), text, guiStyle);
    }
}
