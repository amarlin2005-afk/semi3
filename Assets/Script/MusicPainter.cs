using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 音楽ペイント試作
// ペンでなぞった軌跡と筆圧を、曲の時間と一緒に記録し、水彩のにじみとして描く。
// 記録したテイクは何本でも重ねて再生できる（声部ごとに色を変えて重ねる想定）。
//
// 操作
//   R      : 録音開始（曲が頭から流れ、保存済みテイクも同時に再生される）
//   Space  : 停止して保存
//   Esc    : 停止（保存しない）
//   P      : 保存済みテイクをすべて再生
//   C      : キャンバスを消去（停止中のみ）
//   1〜5   : 筆の色（録音前に選ぶ）
//   H      : 画面上の操作表示の切り替え

[Serializable]
public class StrokeSample
{
    public float t;      // 曲の頭からの秒数
    public float x, y;   // 画面上の位置（0〜1）
    public float p;      // 筆圧（0〜1）
    public bool down;    // ペンが紙に触れているか
}

[Serializable]
public class StrokeTake
{
    public Color color;
    public int seed;
    public List<StrokeSample> samples = new List<StrokeSample>();
}

public class MusicPainter : MonoBehaviour
{
    [Header("音楽")]
    public AudioSource music;

    [Header("筆")]
    public float minRadius = 6f;
    public float maxRadius = 46f;
    [Range(0.05f, 1f)] public float spacing = 0.3f;
    [Range(0f, 1f)] public float minOpacity = 0.03f;
    [Range(0f, 1f)] public float maxOpacity = 0.16f;
    [Tooltip("1より小さいと弱い筆圧でも反応しやすくなる")]
    public float pressureCurve = 0.7f;
    [Range(0f, 1f)] public float granulation = 0.6f;

    [Header("にじみ（筆を置いた瞬間に放射状に広がる）")]
    public int bloomCount = 8;
    public float bloomSpread = 1.6f;
    [Range(0f, 1f)] public float bloomOpacity = 0.06f;

    [Header("紙と色")]
    public Color paperColor = new Color(0.97f, 0.95f, 0.91f);
    public Color[] palette =
    {
        new Color(0.18f, 0.20f, 0.60f), // 藍
        new Color(0.95f, 0.75f, 0.25f), // 黄
        new Color(0.45f, 0.80f, 0.40f), // 緑
        new Color(0.85f, 0.40f, 0.50f), // 薔薇
        new Color(0.45f, 0.33f, 0.25f), // セピア
    };

    struct Stamp
    {
        public Vector2 pos;
        public float r, op, ang;
        public Color col;
    }

    class Player
    {
        public StrokeTake take;
        public int index;
        public StrokeSample prev;
        public int counter;
    }

    RenderTexture canvasRT;
    Material stampMat;
    Texture2D brushTex, paperTex;
    readonly List<Stamp> pending = new List<Stamp>();
    readonly List<Player> players = new List<Player>();
    Player livePlayer;

    bool running, recording, showHud = true;
    double startDsp;
    int colorIndex;
    GUIStyle hud;

    static readonly Key[] digitKeys = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5 };

    int W => canvasRT.width;
    int H => canvasRT.height;
    string TakesDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Takes"));

    void Start()
    {
        canvasRT = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
        canvasRT.Create();

        var shader = Shader.Find("Hidden/WatercolorStamp");
        if (shader == null) { Debug.LogError("WatercolorStamp.shader が見つかりません。Assets に入れてください。"); enabled = false; return; }
        stampMat = new Material(shader);
        brushTex = MakeBrush(128);
        paperTex = MakePaper(512);
        stampMat.SetTexture("_MainTex", brushTex);
        stampMat.SetTexture("_PaperTex", paperTex);

        CreateDisplay();
        ClearCanvas();
        livePlayer = NewLivePlayer();
        Directory.CreateDirectory(TakesDir);
        Debug.Log("テイクの保存先: " + TakesDir);
    }

    void OnDestroy()
    {
        if (canvasRT != null) canvasRT.Release();
    }

    void CreateDisplay()
    {
        var go = new GameObject("PaintCanvas");
        var cv = go.AddComponent<Canvas>();
        cv.renderMode = RenderMode.ScreenSpaceOverlay;
        go.AddComponent<CanvasScaler>();
        var img = new GameObject("Paint").AddComponent<RawImage>();
        img.transform.SetParent(go.transform, false);
        var rt = img.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        img.texture = canvasRT;
        img.raycastTarget = false;
    }

    void Update()
    {
        stampMat.SetFloat("_Granulation", granulation);
        HandleKeys();

        float now = running ? (float)(AudioSettings.dspTime - startDsp) : 0f;

        if (running && now >= 0f)
            foreach (var pl in players) Advance(pl, now);

        if (!running) livePlayer.take.color = palette[colorIndex];

        bool canDraw = !running || (recording && now >= 0f);
        if (canDraw)
        {
            ReadPen(out Vector2 pos, out float p, out bool down);
            var s = new StrokeSample { t = now, x = pos.x / Screen.width, y = pos.y / Screen.height, p = p, down = down };
            Process(livePlayer, s);
            if (recording) livePlayer.take.samples.Add(s);
        }

        Flush();

        if (running && music.clip != null && now > music.clip.length + 0.5f)
            StopSession(recording);
    }

    void HandleKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.rKey.wasPressedThisFrame && !running) StartSession(true);
        if (kb.pKey.wasPressedThisFrame && !running) StartSession(false);
        if (kb.spaceKey.wasPressedThisFrame && running) StopSession(true);
        if (kb.escapeKey.wasPressedThisFrame && running) StopSession(false);
        if (kb.cKey.wasPressedThisFrame && !running) ClearCanvas();
        if (kb.hKey.wasPressedThisFrame) showHud = !showHud;
        if (!running)
            for (int i = 0; i < digitKeys.Length && i < palette.Length; i++)
                if (kb[digitKeys[i]].wasPressedThisFrame) colorIndex = i;
    }

    void ReadPen(out Vector2 pos, out float p, out bool down)
    {
        var pen = Pen.current;
        if (pen != null && (pen.tip.isPressed || pen.inRange.isPressed))
        {
            pos = pen.position.ReadValue();
            p = pen.pressure.ReadValue();
            down = pen.tip.isPressed && p > 0.001f;
            return;
        }
        var m = Mouse.current;
        pos = m != null ? m.position.ReadValue() : Vector2.zero;
        down = m != null && m.leftButton.isPressed;
        p = down ? 0.6f : 0f;
    }

    // ---- セッション ----

    void StartSession(bool record)
    {
        if (music == null || music.clip == null) { Debug.LogError("music に曲の AudioSource を設定してください。"); return; }
        ClearCanvas();
        players.Clear();
        foreach (var take in LoadTakes()) players.Add(new Player { take = take });

        recording = record;
        livePlayer = NewLivePlayer();
        startDsp = AudioSettings.dspTime + 0.3;
        music.Stop();
        music.PlayScheduled(startDsp);
        running = true;
    }

    void StopSession(bool save)
    {
        music.Stop();
        if (save && recording && livePlayer.take.samples.Count > 0) SaveTake(livePlayer.take);
        running = false;
        recording = false;
        livePlayer = NewLivePlayer();
    }

    Player NewLivePlayer()
    {
        return new Player { take = new StrokeTake { color = palette[colorIndex], seed = UnityEngine.Random.Range(1, 100000) } };
    }

    void SaveTake(StrokeTake take)
    {
        Directory.CreateDirectory(TakesDir);
        string path = Path.Combine(TakesDir, "take_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(take));
        Debug.Log("保存しました: " + path);
    }

    List<StrokeTake> LoadTakes()
    {
        var list = new List<StrokeTake>();
        if (!Directory.Exists(TakesDir)) return list;
        var files = Directory.GetFiles(TakesDir, "*.json");
        Array.Sort(files);
        foreach (var f in files)
        {
            var take = JsonUtility.FromJson<StrokeTake>(File.ReadAllText(f));
            if (take != null && take.samples != null) list.Add(take);
        }
        return list;
    }

    // ---- 描画 ----

    void Advance(Player pl, float now)
    {
        var list = pl.take.samples;
        while (pl.index < list.Count && list[pl.index].t <= now)
        {
            Process(pl, list[pl.index]);
            pl.index++;
        }
    }

    void Process(Player pl, StrokeSample s)
    {
        if (s.down)
        {
            var b = new Vector2(s.x * W, s.y * H);
            if (pl.prev != null && pl.prev.down)
            {
                var a = new Vector2(pl.prev.x * W, pl.prev.y * H);
                Segment(pl, a, pl.prev.p, b, s.p);
            }
            else
            {
                Bloom(pl, b, s.p);
                AddStamp(pl, b, s.p);
            }
        }
        pl.prev = s;
    }

    float Curve(float p) => Mathf.Pow(Mathf.Clamp01(p), pressureCurve);
    float Radius(float p) => Mathf.Lerp(minRadius, maxRadius, Curve(p)) * (H / 1080f);

    void Segment(Player pl, Vector2 a, float pa, Vector2 b, float pb)
    {
        float dist = Vector2.Distance(a, b);
        float step = Mathf.Max(1f, Mathf.Min(Radius(pa), Radius(pb)) * spacing);
        int n = Mathf.Max(1, Mathf.CeilToInt(dist / step));
        for (int i = 1; i <= n; i++)
        {
            float k = (float)i / n;
            AddStamp(pl, Vector2.Lerp(a, b, k), Mathf.Lerp(pa, pb, k));
        }
    }

    void AddStamp(Player pl, Vector2 pos, float p)
    {
        float r = Radius(p) * Mathf.Lerp(0.85f, 1.15f, Rnd(pl));
        var jitter = new Vector2(Rnd(pl) - 0.5f, Rnd(pl) - 0.5f) * r * 0.3f;
        pending.Add(new Stamp
        {
            pos = pos + jitter,
            r = r,
            op = Mathf.Lerp(minOpacity, maxOpacity, Curve(p)),
            ang = Rnd(pl) * Mathf.PI * 2f,
            col = pl.take.color
        });
    }

    void Bloom(Player pl, Vector2 c, float p)
    {
        float baseR = Radius(p);
        for (int k = 0; k < bloomCount; k++)
        {
            float ang = (k + Rnd(pl)) / bloomCount * Mathf.PI * 2f;
            float d = baseR * bloomSpread * (0.4f + 0.6f * Rnd(pl));
            pending.Add(new Stamp
            {
                pos = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d,
                r = baseR * (0.6f + 0.8f * Rnd(pl)),
                op = bloomOpacity * Mathf.Max(0.3f, Curve(p)),
                ang = Rnd(pl) * Mathf.PI * 2f,
                col = pl.take.color
            });
        }
    }

    // 同じテイクは何度再生しても同じ形になるよう、乱数を固定する
    float Rnd(Player pl) => Hash(pl.take.seed * 1000003 + pl.counter++);

    static float Hash(int n)
    {
        uint x = (uint)n * 747796405u + 2891336453u;
        x = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
        x = (x >> 22) ^ x;
        return x / 4294967295f;
    }

    void Flush()
    {
        if (pending.Count == 0) return;
        var prevRT = RenderTexture.active;
        RenderTexture.active = canvasRT;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, W, 0, H);
        stampMat.SetPass(0);
        GL.Begin(GL.QUADS);
        foreach (var s in pending)
        {
            GL.Color(new Color(s.col.r, s.col.g, s.col.b, s.op));
            float c = Mathf.Cos(s.ang) * s.r, sn = Mathf.Sin(s.ang) * s.r;
            var ax = new Vector2(c, sn);
            var ay = new Vector2(-sn, c);
            Vertex(s.pos - ax - ay, 0, 0);
            Vertex(s.pos + ax - ay, 1, 0);
            Vertex(s.pos + ax + ay, 1, 1);
            Vertex(s.pos - ax + ay, 0, 1);
        }
        GL.End();
        GL.PopMatrix();
        RenderTexture.active = prevRT;
        pending.Clear();
    }

    void Vertex(Vector2 v, float u, float w)
    {
        GL.MultiTexCoord2(0, u, w);
        GL.MultiTexCoord2(1, v.x / H, v.y / H);
        GL.Vertex3(v.x, v.y, 0);
    }

    void ClearCanvas()
    {
        var prev = RenderTexture.active;
        RenderTexture.active = canvasRT;
        GL.Clear(true, true, paperColor);
        RenderTexture.active = prev;
    }

    // ---- テクスチャ生成 ----

    static Texture2D MakeBrush(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float seed = UnityEngine.Random.value * 100f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var d = new Vector2(x, y) / (size - 1) * 2f - Vector2.one;
            float r = d.magnitude;
            float ang = Mathf.Atan2(d.y, d.x);
            float edge = 0.78f + 0.36f * (Mathf.PerlinNoise(Mathf.Cos(ang) * 1.5f + seed, Mathf.Sin(ang) * 1.5f + seed) - 0.5f);
            float inside = Mathf.Clamp01((edge - r) / 0.08f);
            float rim = Mathf.Exp(-Mathf.Pow((edge - r) / 0.07f, 2f));
            float body = 0.55f + 0.25f * Mathf.PerlinNoise(x * 0.05f + seed, y * 0.05f + seed);
            float a = inside * Mathf.Clamp01(body + rim * 0.6f);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return tex;
    }

    static Texture2D MakePaper(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float n = 0.6f * Mathf.PerlinNoise(x * 0.02f, y * 0.02f) + 0.4f * Mathf.PerlinNoise(x * 0.15f + 50f, y * 0.15f + 50f);
            tex.SetPixel(x, y, new Color(n, n, n, 1f));
        }
        tex.Apply();
        return tex;
    }

    // ---- 操作表示 ----

    void OnGUI()
    {
        if (!showHud) return;
        if (hud == null)
        {
            hud = new GUIStyle(GUI.skin.label) { fontSize = 18 };
            hud.normal.textColor = new Color(0.25f, 0.25f, 0.25f);
        }
        string status;
        if (running)
        {
            float now = Mathf.Max(0f, (float)(AudioSettings.dspTime - startDsp));
            status = (recording ? "● 録音中  " : "▶ 再生中  ") + now.ToString("0.0") + "秒   Space:停止して保存  Esc:破棄";
        }
        else
        {
            status = "R:録音  P:再生  C:消去  1〜5:色（いま " + (colorIndex + 1) + "）  H:表示を隠す";
        }
        GUI.Label(new Rect(16, 12, 1600, 30), status, hud);
    }
}
