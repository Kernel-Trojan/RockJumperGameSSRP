using System.Collections.Generic;
using UnityEngine;

// Endless lake full of rock islands.
//   Water            = game over
//   Rock             = safe to stand on, but running into its SIDE is game over
//   Rock on a rock   = same rules, just taller (darker colour)
// There are no physics colliders: every rock is a flat-topped box in a list, and we ask "how high is the rock under you?"
public class GameManagerVR : MonoBehaviour
{
    public static GameManagerVR Instance;

    public enum State { Menu, Playing, GameOver }

    [System.Serializable]
    public class Wave
    {
        public float amplitude = 0.05f;    // metres (half the wave height)
        public float wavelength = 8f;      // metres from crest to crest
        public float speed = 1.5f;         // how fast the crests travel, metres per second
        public float direction = 0f;       // which way it travels, degrees
    }

    // What is under you: height of the highest rock top, whether that rock is a stacked one, and which rock it is
    public struct Probe
    {
        public float top;
        public bool stack;
        public int id;
    }

    [Header("Drag these in")]
    public PinchJumpVR player;
    public GameObject rockModel;             // drag the RockSlab model here

    [Header("World (1 unit = 1 metre)")]
    public float gravity = 14.7f;
    public float waterLevel = 0f;            // average height of the water surface
    public float startTop = 0.8f;            // height of the rock you start on
    public float startPlatformSize = 12f;
    public float startClearRadius = 7.5f;    // no other rocks this close to the start
    public float rockSink = 0.3f;            // rocks reach this far below the water so waves never show a gap under them

    [Header("Rock islands")]
    public float cellSize = 6.2f;           // spacing of the island grid (bigger = wider water gaps)
    public float minIslandSize = 5.2f;       // width of an island in metres
    public float maxIslandSize = 6.4f;
    public float minTop = 0.5f;              // lowest rock top above the water
    public float maxTop = 1.3f;              // highest rock top above the water
    [Range(0f, 1f)] public float stackChance = 0.5f;   // chance that a smaller rock stands on top of an island
    public float minStackHeight = 0.35f;
    public float maxStackHeight = 0.8f;
    public float renderRadius = 42f;
    public int seed = 12345;

    [Header("Water waves (a mix of sine waves)")]
    public int waterGrid = 64;               // squares per side (more = smoother but slower)
    public float waterSize = 84f;            // metres, always centred on you
    public Color waterColor = new Color(0.10f, 0.38f, 0.60f);
    public float waveStrength = 1f;          // multiplies every amplitude (0 = flat water)
    public Wave[] waves;                     // leave empty to get a good default mix

    [Header("Sky")]
    public bool useSky = true;
    public float sunSize = 0.05f;
    public float atmosphere = 1f;            // 0.5 = thin and dark blue, 2+ = hazy sunset
    public float skyExposure = 1.3f;

    [Header("Steering")]
    public bool steerByGaze = true;          // the world flows in the direction you are looking
    public float turnRate = 90f;             // max degrees per second the travel direction can swing (lower = more comfortable)

    [Header("Speed (metres per second)")]
    public float startSpeed = 3f;
    public float maxSpeed = 8f;
    public float speedGainPerUnit = 0.01f;   // extra speed for every metre travelled

    [Header("Points")]
    public float pointsPerMetre = 1f;
    public int rockBonus = 10;               // first landing on a new rock
    public int stackBonus = 25;              // first landing on a rock that sits on another rock
    public bool startOnForcePress = true;    // squeeze the sensor to press Play / Play again
    public float restartDelay = 1f;          // seconds after game over before the sensor can restart

    [System.NonSerialized] public State state = State.Menu;
    [System.NonSerialized] public float speed;
    [System.NonSerialized] public float distance;
    [System.NonSerialized] public string gameOverReason = "";
    [System.NonSerialized] public Vector3 heading = Vector3.forward;   // the direction you are "running" in

    public bool IsPlaying { get { return state == State.Playing; } }
    public bool gameOver { get { return state == State.GameOver; } }
    public int Score { get { return Mathf.FloorToInt(distance * pointsPerMetre) + bonus; } }

    class RockBox
    {
        public int id;
        public bool isStack;
        public Vector3 centre;
        public Quaternion invRot;
        public float halfX;
        public float halfZ;
        public float top;
        public float reach2;
    }

    class Cell
    {
        public GameObject container;
        public List<RockBox> boxes = new List<RockBox>();
    }

    float headingAngle;     // degrees, 0 = +Z (straight ahead), 90 = +X
    Camera cam;
    Material[] rockMaterials;
    Material[] stackMaterials;
    static Material baseMaterial;

    readonly Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();
    readonly List<RockBox> allBoxes = new List<RockBox>();
    Vector2Int lastPlayerCell = new Vector2Int(int.MinValue, int.MinValue);
    int nextRockId;
    int startRockId = -1;

    // points
    int bonus;
    int highScore;
    bool newBest;
    float gameOverTime;
    string popup = "";
    float popupTime = -10f;
    readonly HashSet<int> rewarded = new HashSet<int>();

    // water
    GameObject waterObj;
    Mesh waterMesh;
    Vector2[] waterBase;
    Vector3[] waterVerts;
    Vector3[] waterNormals;
    float[] wKx, wKz, wOm, wAm;   // per-wave numbers, reused every frame (no garbage)

    // on-screen styles
    GUIStyle hudStyle, bigStyle, midStyle, buttonStyle;

    // Where you are in the (static) rock world. The manager object moves; you stay at the origin.
    Vector3 PlayerStatic
    {
        get { return new Vector3(-transform.position.x, 0f, -transform.position.z); }
    }

    void Awake()
    {
        Instance = this;
        speed = startSpeed;

        // The hit tests assume this object starts at the origin, unrotated and unscaled
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        highScore = PlayerPrefs.GetInt("HighScoreVR", 0);

        // Fog hides the far edge of the lake and rocks popping into view
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.70f, 0.80f, 0.90f);
        RenderSettings.fogStartDistance = 15f;
        RenderSettings.fogEndDistance = 38f;
    }

    void Start()
    {
        // Your old flat Ground/Water cube is replaced by the wavy water below, so hide it
        GameObject old = GameObject.Find("Water");
        if (old == null) old = GameObject.Find("Ground");
        if (old != null)
        {
            Renderer r = old.GetComponent<Renderer>();
            if (r != null) r.enabled = false;
        }

        SetupSky();
        BuildWater();
        BuildRockMaterials();
        ResetWorld();
    }

    void Update()
    {
        UpdateWater();

        if (Input.GetKeyDown(KeyCode.R)) ResetWorld();

        if (state == State.Playing)
        {
            UpdateHeading();

            distance += speed * Time.deltaTime;
            speed = Mathf.Min(startSpeed + distance * speedGainPerUnit, maxSpeed);

            // You stay in the middle. The whole world (every rock is a child of this object)
            // flows past you, opposite to the direction you are heading.
            transform.position -= heading * speed * Time.deltaTime;

            UpdateCells(false);
        }
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            Play();
        }
    }

    // ---------- Game flow ----------

    // Start a fresh run
    public void Play()
    {
        if (state == State.Playing) return;

        ResetWorld();
        SnapHeadingToGaze();
        state = State.Playing;
    }

    // The sensor script / player calls this when the force sensor is squeezed on the menu or game over screen
    public void RequestPlayFromSensor()
    {
        if (!startOnForcePress) return;
        if (state == State.Menu) Play();
        else if (state == State.GameOver && Time.time - gameOverTime > restartDelay) Play();
    }

    // Back to the start platform, waiting for Play
    public void ResetWorld()
    {
        foreach (KeyValuePair<Vector2Int, Cell> kv in cells) Destroy(kv.Value.container);
        cells.Clear();
        allBoxes.Clear();
        transform.position = Vector3.zero;

        distance = 0f;
        speed = startSpeed;
        bonus = 0;
        newBest = false;
        popup = "";
        gameOverReason = "";
        headingAngle = 0f;
        heading = Vector3.forward;
        lastPlayerCell = new Vector2Int(int.MinValue, int.MinValue);
        state = State.Menu;

        startRockId = -1;
        UpdateCells(true);

        rewarded.Clear();
        if (startRockId >= 0) rewarded.Add(startRockId);   // standing where you began earns nothing

        if (player != null) player.ResetPlayer();
    }

    public void GameOver(string reason)
    {
        if (state != State.Playing) return;
        state = State.GameOver;
        gameOverReason = reason;
        gameOverTime = Time.time;

        if (Score > highScore)
        {
            highScore = Score;
            newBest = true;
            PlayerPrefs.SetInt("HighScoreVR", highScore);
            PlayerPrefs.Save();
        }
    }

    // Called when you land on something. A rock only pays out once per run.
    public void NotifyLanding(int rockId, bool isStack)
    {
        if (state != State.Playing || rockId < 0) return;
        if (!rewarded.Add(rockId)) return;

        int pts = isStack ? stackBonus : rockBonus;
        bonus += pts;
        popup = "+" + pts + (isStack ? "  stacked rock!" : "  new rock");
        popupTime = Time.time;
    }

    // ---------- Steering ----------

    void FindCamera()
    {
        if (cam == null) cam = Camera.main;
    }

    // Travel direction follows where your head points (smoothly), or stays straight if steerByGaze is off
    void UpdateHeading()
    {
        if (steerByGaze)
        {
            float target;
            if (GazeYaw(out target))
                headingAngle = Mathf.MoveTowardsAngle(headingAngle, target, turnRate * Time.deltaTime);
        }

        float a = headingAngle * Mathf.Deg2Rad;
        heading = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
    }

    // At the start of a run, begin going where you are already looking (no slow swing-round)
    void SnapHeadingToGaze()
    {
        float target;
        if (steerByGaze && GazeYaw(out target)) headingAngle = target;
        float a = headingAngle * Mathf.Deg2Rad;
        heading = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
    }

    bool GazeYaw(out float yaw)
    {
        yaw = 0f;
        FindCamera();
        if (cam == null) return false;

        Vector3 f = cam.transform.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 0.05f) return false;   // ignore when looking straight up or down
        yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        return true;
    }

    // ---------- Asking the world: how high is the rock under me? ----------

    // Looks under your body at 5 points (centre, front, back, left, right).
    // worldPos is relative to you (you are at the origin). top is -infinity if there is only water.
    public Probe ProbeSurface(Vector3 worldPos, Vector3 dir, float radius)
    {
        Vector3 side = new Vector3(dir.z, 0f, -dir.x);

        Probe best = new Probe();
        best.top = float.NegativeInfinity;
        best.id = -1;

        ProbeAt(worldPos, ref best);
        ProbeAt(worldPos + dir * radius, ref best);
        ProbeAt(worldPos - dir * radius, ref best);
        ProbeAt(worldPos + side * radius, ref best);
        ProbeAt(worldPos - side * radius, ref best);
        return best;
    }

    public float SurfaceHeight(Vector3 worldPos, Vector3 dir, float radius)
    {
        return ProbeSurface(worldPos, dir, radius).top;
    }

    void ProbeAt(Vector3 worldPos, ref Probe best)
    {
        Vector3 p = worldPos - transform.position;   // into the static rock world

        for (int i = 0; i < allBoxes.Count; i++)
        {
            RockBox b = allBoxes[i];
            if (b.top <= best.top) continue;

            float dx = p.x - b.centre.x;
            float dz = p.z - b.centre.z;
            if (dx * dx + dz * dz > b.reach2) continue;

            Vector3 l = b.invRot * new Vector3(dx, 0f, dz);
            if (Mathf.Abs(l.x) <= b.halfX && Mathf.Abs(l.z) <= b.halfZ)
            {
                best.top = b.top;
                best.stack = b.isStack;
                best.id = b.id;
            }
        }
    }

    // ---------- Building the lake ----------

    Vector3 CellCentre(Vector2Int c)
    {
        return new Vector3((c.x + 0.5f) * cellSize, 0f, (c.y + 0.5f) * cellSize);
    }

    void UpdateCells(bool force)
    {
        Vector3 pp = PlayerStatic;
        Vector2Int pc = new Vector2Int(Mathf.FloorToInt(pp.x / cellSize), Mathf.FloorToInt(pp.z / cellSize));
        if (!force && pc == lastPlayerCell) return;
        lastPlayerCell = pc;

        int range = Mathf.CeilToInt(renderRadius / cellSize);

        // create cells that came into range
        for (int dx = -range; dx <= range; dx++)
        {
            for (int dz = -range; dz <= range; dz++)
            {
                Vector2Int c = new Vector2Int(pc.x + dx, pc.y + dz);
                if (cells.ContainsKey(c)) continue;

                Vector3 cc = CellCentre(c);
                if (new Vector2(cc.x - pp.x, cc.z - pp.z).magnitude > renderRadius) continue;

                BuildCell(c);
            }
        }

        // remove cells that are far behind
        List<Vector2Int> remove = null;
        foreach (KeyValuePair<Vector2Int, Cell> kv in cells)
        {
            Vector3 cc = CellCentre(kv.Key);
            if (new Vector2(cc.x - pp.x, cc.z - pp.z).magnitude > renderRadius + cellSize * 1.5f)
            {
                if (remove == null) remove = new List<Vector2Int>();
                remove.Add(kv.Key);
            }
        }
        if (remove != null)
        {
            foreach (Vector2Int c in remove)
            {
                Destroy(cells[c].container);
                cells.Remove(c);
            }
        }

        allBoxes.Clear();
        foreach (KeyValuePair<Vector2Int, Cell> kv in cells) allBoxes.AddRange(kv.Value.boxes);
    }

    void BuildCell(Vector2Int c)
    {
        Cell cell = new Cell();
        cell.container = new GameObject("Cell " + c.x + "," + c.y);
        cell.container.transform.SetParent(transform, false);
        cells[c] = cell;

        // The rock you start on
        if (c.x == 0 && c.y == 0)
        {
            startRockId = nextRockId;
            AddRock(cell, Vector3.zero, 0f, startPlatformSize, startPlatformSize, -rockSink, startTop + rockSink, false);
            return;
        }

        // Keep the water around the start platform free of other rocks
        Vector3 cc = CellCentre(c);
        if (new Vector2(cc.x, cc.z).magnitude < startClearRadius) return;

        // The same cell always gets the same rocks (so rocks do not change if you come back)
        System.Random rnd = new System.Random((seed * 31 + c.x) * 7919 + c.y * 104729);

        float w = Mathf.Lerp(minIslandSize, maxIslandSize, (float)rnd.NextDouble());
        float d = Mathf.Lerp(minIslandSize, maxIslandSize, (float)rnd.NextDouble());
        float jx = ((float)rnd.NextDouble() - 0.5f) * 0.8f;
        float jz = ((float)rnd.NextDouble() - 0.5f) * 0.8f;
        float yaw = ((float)rnd.NextDouble() - 0.5f) * 40f;
        float top = Mathf.Lerp(minTop, maxTop, (float)rnd.NextDouble());

        Vector3 centre = cc + new Vector3(jx, 0f, jz);
        AddRock(cell, centre, yaw, w, d, -rockSink, top + rockSink, false);   // top of the rock stays exactly at 'top'

        // Sometimes a smaller rock stands on top of the island
        if (rnd.NextDouble() < stackChance)
        {
            float sw = w * Mathf.Lerp(0.35f, 0.55f, (float)rnd.NextDouble());
            float sd = d * Mathf.Lerp(0.35f, 0.55f, (float)rnd.NextDouble());
            float sh = Mathf.Lerp(minStackHeight, maxStackHeight, (float)rnd.NextDouble());
            float ox = ((float)rnd.NextDouble() - 0.5f) * (w - sw - 0.8f);
            float oz = ((float)rnd.NextDouble() - 0.5f) * (d - sd - 0.8f);
            float syaw = yaw + ((float)rnd.NextDouble() - 0.5f) * 30f;

            Vector3 sc = centre + Quaternion.Euler(0f, yaw, 0f) * new Vector3(ox, 0f, oz);
            AddRock(cell, sc, syaw, sw, sd, top, sh, true);
        }
    }

    // One rock: a visible model, plus a box (centre, turn, width, depth, bottom, height) used for the height questions
    void AddRock(Cell cell, Vector3 centre, float yaw, float w, float d, float baseY, float height, bool isStack)
    {
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);

        GameObject o = rockModel != null ? Instantiate(rockModel) : GameObject.CreatePrimitive(PrimitiveType.Cube);
        o.name = isStack ? "StackedRock" : "Rock";
        foreach (Collider col in o.GetComponentsInChildren<Collider>()) Destroy(col);
        o.transform.SetParent(cell.container.transform, false);

        // Scale the model so it comes out exactly (w x height x d) metres, bottom at baseY, whatever its size or pivot
        Vector3 size = Vector3.one;
        float bottom = 0f;
        MeshFilter mf = o.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            size = mf.sharedMesh.bounds.size;
            bottom = mf.sharedMesh.bounds.min.y;
        }

        float sx = w / Mathf.Max(size.x, 0.0001f);
        float sy = height / Mathf.Max(size.y, 0.0001f);
        float sz = d / Mathf.Max(size.z, 0.0001f);

        o.transform.localScale = new Vector3(sx, sy, sz);
        o.transform.localRotation = rot;
        o.transform.localPosition = new Vector3(centre.x, baseY - bottom * sy, centre.z);

        Material[] set = isStack ? stackMaterials : rockMaterials;
        Material m = set[Random.Range(0, set.Length)];
        foreach (Renderer r in o.GetComponentsInChildren<Renderer>()) r.sharedMaterial = m;

        RockBox b = new RockBox();
        b.id = nextRockId++;
        b.isStack = isStack;
        b.centre = centre;
        b.invRot = Quaternion.Inverse(rot);
        b.halfX = w * 0.5f * 0.95f;     // a little inside the visible edge
        b.halfZ = d * 0.5f * 0.95f;
        b.top = baseY + height;
        b.reach2 = b.halfX * b.halfX + b.halfZ * b.halfZ;
        cell.boxes.Add(b);
    }

    void BuildRockMaterials()
    {
        rockMaterials = new Material[4];
        for (int i = 0; i < rockMaterials.Length; i++)
        {
            Material m = new Material(GetBaseMaterial());
            m.color = Color.Lerp(new Color(0.40f, 0.39f, 0.37f), new Color(0.60f, 0.58f, 0.54f), i / 3f);
            rockMaterials[i] = m;
        }

        // Stacked rocks are darker so you can tell them apart from the islands
        stackMaterials = new Material[2];
        for (int i = 0; i < stackMaterials.Length; i++)
        {
            Material m = new Material(GetBaseMaterial());
            m.color = Color.Lerp(new Color(0.27f, 0.25f, 0.24f), new Color(0.38f, 0.34f, 0.31f), i);
            stackMaterials[i] = m;
        }
    }

    // The project's normal default material (avoids pink materials in any render pipeline)
    public static Material GetBaseMaterial()
    {
        if (baseMaterial == null)
        {
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseMaterial = tmp.GetComponent<Renderer>().sharedMaterial;
            Destroy(tmp);
        }
        return baseMaterial;
    }

    // ---------- Sky ----------

    void SetupSky()
    {
        if (!useSky) return;

        FindCamera();

        Shader sh = Shader.Find("Skybox/Procedural");
        if (sh != null)
        {
            Material sky = new Material(sh);
            if (sky.HasProperty("_SunSize")) sky.SetFloat("_SunSize", sunSize);
            if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", atmosphere);
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", skyExposure);
            if (sky.HasProperty("_SkyTint")) sky.SetColor("_SkyTint", new Color(0.5f, 0.5f, 0.5f));
            if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.55f, 0.65f, 0.75f));
            RenderSettings.skybox = sky;

            if (cam != null) cam.clearFlags = CameraClearFlags.Skybox;
        }
        else if (cam != null)
        {
            // Skybox shader not available: plain blue sky instead
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.45f, 0.65f, 0.90f);
        }
    }

    // ---------- Waves ----------

    static Wave MakeWave(float amplitude, float wavelength, float speed, float direction)
    {
        Wave w = new Wave();
        w.amplitude = amplitude;
        w.wavelength = wavelength;
        w.speed = speed;
        w.direction = direction;
        return w;
    }

    // Big slow swell + smaller, faster ripples going in different directions
    static Wave[] DefaultWaves()
    {
        return new Wave[]
        {
            MakeWave(0.080f, 16f, 1.6f,  10f),
            MakeWave(0.050f,  9.5f, 1.3f,  70f),
            MakeWave(0.030f,  6f,  1.9f, -35f),
            MakeWave(0.018f,  4f,  1.1f, 120f),
            MakeWave(0.010f,  3f,  2.2f, -80f),
        };
    }

    void BuildWater()
    {
        if (waves == null || waves.Length == 0) waves = DefaultWaves();

        int n = Mathf.Max(8, waterGrid);
        int vc = (n + 1) * (n + 1);
        waterBase = new Vector2[vc];
        waterVerts = new Vector3[vc];
        waterNormals = new Vector3[vc];

        float step = waterSize / n;
        float half = waterSize * 0.5f;
        for (int z = 0; z <= n; z++)
        {
            for (int x = 0; x <= n; x++)
            {
                int i = z * (n + 1) + x;
                waterBase[i] = new Vector2(-half + x * step, -half + z * step);
                waterVerts[i] = new Vector3(waterBase[i].x, waterLevel, waterBase[i].y);
                waterNormals[i] = Vector3.up;
            }
        }

        int[] tris = new int[n * n * 6];
        int t = 0;
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                int v0 = z * (n + 1) + x;
                int vX = v0 + 1;
                int vZ = v0 + (n + 1);
                int vXZ = vZ + 1;

                tris[t++] = v0; tris[t++] = vZ; tris[t++] = vX;
                tris[t++] = vX; tris[t++] = vZ; tris[t++] = vXZ;
            }
        }

        waterMesh = new Mesh();
        waterMesh.name = "WaterMesh";
        waterMesh.MarkDynamic();
        waterMesh.vertices = waterVerts;
        waterMesh.normals = waterNormals;
        waterMesh.triangles = tris;
        waterMesh.bounds = new Bounds(new Vector3(0f, waterLevel, 0f), new Vector3(waterSize, 10f, waterSize));

        waterObj = new GameObject("WaterSurface");
        waterObj.transform.position = Vector3.zero;
        waterObj.AddComponent<MeshFilter>().sharedMesh = waterMesh;
        MeshRenderer mr = waterObj.AddComponent<MeshRenderer>();

        Material m = new Material(GetBaseMaterial());
        m.color = waterColor;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.85f);   // built-in pipeline
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.85f);   // URP
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        mr.sharedMaterial = m;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // Height = sum of sines. The waves are glued to the ROCK world (not to you), so they drift past as you move,
    // which also gives you a sense of speed.
    void UpdateWater()
    {
        if (waterMesh == null) return;

        int wc = waves.Length;
        if (wKx == null || wKx.Length != wc)
        {
            wKx = new float[wc];
            wKz = new float[wc];
            wOm = new float[wc];
            wAm = new float[wc];
        }
        for (int j = 0; j < wc; j++)
        {
            float k = 2f * Mathf.PI / Mathf.Max(waves[j].wavelength, 0.1f);
            float a = waves[j].direction * Mathf.Deg2Rad;
            wKx[j] = k * Mathf.Sin(a);
            wKz[j] = k * Mathf.Cos(a);
            wOm[j] = k * waves[j].speed;
            wAm[j] = waves[j].amplitude * waveStrength;
        }

        float t = Time.time;
        float offX = transform.position.x;
        float offZ = transform.position.z;

        for (int i = 0; i < waterBase.Length; i++)
        {
            float sx = waterBase[i].x - offX;   // position in the static world
            float sz = waterBase[i].y - offZ;

            float h = 0f, dhx = 0f, dhz = 0f;
            for (int j = 0; j < wc; j++)
            {
                float ph = wKx[j] * sx + wKz[j] * sz - wOm[j] * t;
                h += wAm[j] * Mathf.Sin(ph);
                float c = wAm[j] * Mathf.Cos(ph);
                dhx += c * wKx[j];
                dhz += c * wKz[j];
            }

            waterVerts[i].y = waterLevel + h;
            waterNormals[i] = new Vector3(-dhx, 1f, -dhz).normalized;
        }

        waterMesh.vertices = waterVerts;
        waterMesh.normals = waterNormals;
    }

    // ---------- On-screen display (desktop only - OnGUI does not show inside a headset) ----------

    void MakeStyles(float u)
    {
        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.label);
            hudStyle.normal.textColor = Color.white;
            bigStyle = new GUIStyle(GUI.skin.label);
            bigStyle.alignment = TextAnchor.MiddleCenter;
            bigStyle.normal.textColor = Color.white;
            midStyle = new GUIStyle(GUI.skin.label);
            midStyle.alignment = TextAnchor.MiddleCenter;
            midStyle.normal.textColor = Color.white;
            buttonStyle = new GUIStyle(GUI.skin.button);
        }
        hudStyle.fontSize = Mathf.RoundToInt(2.6f * u);
        bigStyle.fontSize = Mathf.RoundToInt(7f * u);
        midStyle.fontSize = Mathf.RoundToInt(3.2f * u);
        buttonStyle.fontSize = Mathf.RoundToInt(3.4f * u);
    }

    void OnGUI()
    {
        float u = Screen.height / 100f;    // 1% of the screen height
        float sw = Screen.width;
        float sh = Screen.height;
        MakeStyles(u);

        // Score panel
        GUI.Label(new Rect(2 * u, 1.5f * u, 40 * u, 4 * u), "Score  " + Score, hudStyle);
        GUI.Label(new Rect(2 * u, 5.2f * u, 40 * u, 4 * u), "Best   " + highScore, hudStyle);
        GUI.Label(new Rect(2 * u, 8.9f * u, 40 * u, 4 * u), "Distance  " + Mathf.FloorToInt(distance) + " m", hudStyle);
        GUI.Label(new Rect(2 * u, 12.6f * u, 40 * u, 4 * u), "Speed  " + speed.ToString("F1") + " m/s", hudStyle);

        // Points popup
        if (state == State.Playing && Time.time - popupTime < 1.3f)
            GUI.Label(new Rect(0, sh * 0.22f, sw, 6 * u), popup, midStyle);

        if (state == State.Playing)
        {
            if (GUI.Button(new Rect(sw - 16 * u, 1.5f * u, 14 * u, 5.5f * u), "RESET", buttonStyle)) ResetWorld();
        }
        else if (state == State.Menu)
        {
            GUI.Label(new Rect(0, sh * 0.22f, sw, 10 * u), "READY", bigStyle);
            GUI.Label(new Rect(0, sh * 0.22f + 10 * u, sw, 5 * u), "Hold Space (or squeeze the sensor) to charge, release to jump", midStyle);
            GUI.Label(new Rect(0, sh * 0.22f + 15 * u, sw, 5 * u), "Look where you want to go. Stay off the water!", midStyle);
            if (GUI.Button(new Rect(sw * 0.5f - 12 * u, sh * 0.55f, 24 * u, 8 * u), "PLAY", buttonStyle)) Play();
        }
        else
        {
            GUI.Label(new Rect(0, sh * 0.2f, sw, 10 * u), "GAME OVER", bigStyle);
            GUI.Label(new Rect(0, sh * 0.2f + 10 * u, sw, 5 * u), gameOverReason, midStyle);
            GUI.Label(new Rect(0, sh * 0.2f + 15 * u, sw, 5 * u),
                "Score " + Score + (newBest ? "   NEW BEST!" : "   Best " + highScore), midStyle);
            if (GUI.Button(new Rect(sw * 0.5f - 25 * u, sh * 0.55f, 24 * u, 8 * u), "PLAY AGAIN", buttonStyle)) Play();
            if (GUI.Button(new Rect(sw * 0.5f + 1 * u, sh * 0.55f, 24 * u, 8 * u), "RESET", buttonStyle)) ResetWorld();
        }
    }
}
