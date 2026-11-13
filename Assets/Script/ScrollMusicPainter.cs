using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 音楽ペイント（流れる五線譜・マルチパート版）
// パートごとに音源と線の色を持ち、DAWのようにミュート／ソロできる。
// テイクは録音したときのパートに結びつき、ミュートしたパートは音も線も消える。
//
// 操作
//   1〜9   : 録音するパートを選ぶ（画面左上のパネルの ● でも選べる）
//   M / S  : 選んでいるパートのミュート／ソロ（パネルの M・S ボタンでも切り替え可）
//   R      : 録音開始（選んでいるパートのテイクとして記録）
//   Space  : 停止して保存
//   Esc    : 停止（保存しない）
//   P      : 再生（描いたときのタイミングで色づいていく）
//   D（押している間）: 読み進める（再生）。赤い線が触れた筆跡から色づく
//   A（押している間）: 読み返す（逆再生）。色はそのまま残る
//   Shift + A/D : ゆっくり（0.5倍。音程も下がる）
//   C      : モノクロに戻す
//   停止中 : ペン／マウスのドラッグ、ホイール、←→キーで帯をスクロール
//   F      : 帯の全体表示の切り替え
//   Home   : 帯の先頭へ
//   H      : 操作表示とパネルの切り替え（録画するときは隠す）
//   ※逆再生には、各音源の Load Type を「Decompress On Load」にしておく必要がある

[Serializable]
public class ScrollSample
{
    public float t;     // 描いた時刻（曲の頭からの秒）
    public float sec;   // 紙の上の位置（曲の何秒目の場所か）
    public float y;     // 高さ（0〜1）
    public float p;     // 筆圧（0〜1）
    public bool down;
}

[Serializable]
public class ScrollTake
{
    public string part;  // どのパートのテイクか（パート名）
    public Color color;
    public int seed;
    public List<ScrollSample> samples = new List<ScrollSample>();
}

[Serializable]
public class PartTrack
{
    public string name = "パート";
    public AudioClip clip;
    public Color color = new Color(0.18f, 0.20f, 0.60f);
    public bool mute;
    public bool solo;
}

public class ScrollMusicPainter : MonoBehaviour
{
    [Header("パート（音源・線の色・ミュート・ソロ）")]
    [Tooltip("パート名でテイクと結びつくので、録音後に名前を変えると線がどのパートか分からなくなる")]
    public List<PartTrack> parts = new List<PartTrack>
    {
        new PartTrack { name = "メロディ", color = new Color(0.18f, 0.20f, 0.60f) },
        new PartTrack { name = "ハモリ上", color = new Color(0.95f, 0.70f, 0.20f) },
        new PartTrack { name = "ハモリ下", color = new Color(0.40f, 0.75f, 0.40f) },
    };

    [Header("流れる紙")]
    [Tooltip("1秒が何ピクセルぶん流れるか。大きいほど速く流れ、細かく描ける")]
    public float pixelsPerSecond = 220f;
    [Tooltip("いま鳴っている位置を示す線の、画面上の横位置（0=左端、1=右端）")]
    [Range(0.1f, 0.9f)] public float playheadPosition = 0.35f;

    [Header("モノクロの下書き")]
    [Range(0f, 1f)] public float monoStrength = 0.7f;

    [Header("五線と小節線")]
    public float bpm = 126f;
    public int beatsPerBar = 4;
    [Tooltip("曲の頭から1拍目までの秒数")]
    public float firstBeatOffset = 0f;
    public float staffSpacing = 70f;
    public Color lineColor = new Color(0.3f, 0.3f, 0.38f, 0.35f);
    public Color playheadColor = new Color(0.85f, 0.35f, 0.35f, 0.6f);

    [Header("筆")]
    public float minRadius = 6f;
    public float maxRadius = 46f;
    [Range(0.05f, 1f)] public float spacing = 0.3f;
    [Range(0f, 1f)] public float minOpacity = 0.03f;
    [Range(0f, 1f)] public float maxOpacity = 0.16f;
    public float pressureCurve = 0.7f;
    [Range(0f, 1f)] public float granulation = 0.6f;

    [Header("にじみ（筆を置いた瞬間に放射状に広がる）")]
    public int bloomCount = 8;
    public float bloomSpread = 1.6f;
    [Range(0f, 1f)] public float bloomOpacity = 0.06f;

    [Header("紙")]
    public Color paperColor = new Color(0.97f, 0.95f, 0.91f);

    const int TW = 2048; // 紙を分割するタイルの幅（ピクセル）

    struct Stamp
    {
        public Vector2 pos;
        public float r, op, ang;
        public Color col;
        public int part;
    }

    class PartRuntime
    {
        public AudioSource src;
        public RectTransform root;
        public List<RenderTexture> tiles = new List<RenderTexture>();
        public List<RawImage> imgs = new List<RawImage>();
        public List<int>[] byTile = new List<int>[0];
    }

    class TakeState
    {
        public ScrollTake take;
        public int part;
        public bool[] colored;
    }

    int H;
    Material stampMat, layerMat;
    readonly List<PartRuntime> rts = new List<PartRuntime>();
    readonly List<Stamp> pending = new List<Stamp>();
    readonly List<TakeState> takes = new List<TakeState>();
    ScrollTake liveTake;
    int livePart, currentPart, masterIndex = -1;

    RectTransform container, linesRoot;
    Image playhead;
    Rect panelRect;

    bool running, recording, fit, showHud = true;
    bool reading;
    int readDir;
    double startDsp;
    float viewLeftSec, totalSec, lastColorT;
    GUIStyle hud, rowLabel;

    static readonly Key[] digitKeys =
        { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };

    string TakesDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ScrollTakes"));
    float PlayheadOffsetSec => playheadPosition * Screen.width / pixelsPerSecond;
    AudioSource Master => masterIndex >= 0 ? rts[masterIndex].src : null;

    float SongLength
    {
        get
        {
            float len = 0f;
            foreach (var p in parts) if (p.clip != null) len = Mathf.Max(len, p.clip.length);
            return len > 0f ? len : 30f;
        }
    }

    void Start()
    {
        H = Screen.height;
        var stampShader = Shader.Find("Hidden/WatercolorStamp");
        var layerShader = Shader.Find("Hidden/MultiplyLayer");
        if (stampShader == null || layerShader == null)
        {
            Debug.LogError("WatercolorStamp.shader と MultiplyLayer.shader を Assets に入れてください。");
            enabled = false;
            return;
        }
        if (parts.Count == 0)
        {
            Debug.LogError("Parts にパートを1つ以上追加してください。");
            enabled = false;
            return;
        }
        stampMat = new Material(stampShader);
        stampMat.SetTexture("_MainTex", MakeBrush(128));
        stampMat.SetTexture("_PaperTex", MakePaper(512));
        layerMat = new Material(layerShader);

        float longest = -1f;
        for (int i = 0; i < parts.Count; i++)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.clip = parts[i].clip;
            rts.Add(new PartRuntime { src = src });
            if (parts[i].clip != null && parts[i].clip.length > longest)
            {
                longest = parts[i].clip.length;
                masterIndex = i;
            }
        }

        BuildUI();
        Directory.CreateDirectory(TakesDir);
        Debug.Log("テイクの保存先: " + TakesDir);
        ShowMonochrome();
        viewLeftSec = -PlayheadOffsetSec;
    }

    void OnDestroy()
    {
        foreach (var r in rts)
            foreach (var t in r.tiles)
                if (t != null) t.Release();
    }

    // ---- ミュートとソロ ----

    bool AnySolo()
    {
        foreach (var p in parts) if (p.solo) return true;
        return false;
    }

    bool Audible(int i) => AnySolo() ? parts[i].solo : !parts[i].mute;

    void ApplyMuteSolo()
    {
        for (int i = 0; i < rts.Count; i++)
        {
            bool on = Audible(i);
            rts[i].src.mute = !on;
            if (rts[i].root != null && rts[i].root.gameObject.activeSelf != on)
                rts[i].root.gameObject.SetActive(on);
        }
    }

    // ---- 画面の組み立て ----

    RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        return rt;
    }

    void BuildUI()
    {
        var cgo = new GameObject("ScrollPaper");
        var canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var bg = NewRect("Background", cgo.transform);
        bg.anchorMax = Vector2.one;
        bg.offsetMin = bg.offsetMax = Vector2.zero;
        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.color = paperColor;
        bgImg.raycastTarget = false;

        container = NewRect("Band", cgo.transform);
        for (int i = 0; i < parts.Count; i++)
            rts[i].root = NewRect("Part_" + parts[i].name, container);
        linesRoot = NewRect("Lines", container);

        var ph = NewRect("Playhead", cgo.transform);
        ph.anchoredPosition = new Vector2(playheadPosition * Screen.width - 1f, 0f);
        ph.sizeDelta = new Vector2(2f, H);
        playhead = ph.gameObject.AddComponent<Image>();
        playhead.color = playheadColor;
        playhead.raycastTarget = false;
    }

    void ResetPaper(float songSeconds)
    {
        totalSec = songSeconds + 3f;
        int need = Mathf.CeilToInt(totalSec * pixelsPerSecond / TW);

        foreach (var r in rts)
        {
            foreach (var t in r.tiles) t.Release();
            foreach (var img in r.imgs) Destroy(img.gameObject);
            r.tiles.Clear();
            r.imgs.Clear();

            for (int i = 0; i < need; i++)
            {
                var tex = new RenderTexture(TW, H, 0, RenderTextureFormat.ARGB32);
                tex.Create();
                r.tiles.Add(tex);
                var rect = NewRect("Tile" + i, r.root);
                rect.anchoredPosition = new Vector2(i * TW, 0f);
                rect.sizeDelta = new Vector2(TW, H);
                var img = rect.gameObject.AddComponent<RawImage>();
                img.texture = tex;
                img.material = layerMat;
                img.raycastTarget = false;
                r.imgs.Add(img);
            }
            r.byTile = new List<int>[need];
            for (int i = 0; i < need; i++) r.byTile[i] = new List<int>();
        }

        ClearTiles();
        BuildLines();
    }

    void BuildLines()
    {
        for (int i = linesRoot.childCount - 1; i >= 0; i--) Destroy(linesRoot.GetChild(i).gameObject);

        float totalPx = (rts.Count > 0 ? rts[0].tiles.Count : 1) * TW;
        float center = H * 0.5f;
        float bottom = center - 2f * staffSpacing;

        for (int k = 0; k < 5; k++)
            AddLine(0f, bottom + k * staffSpacing - 1f, totalPx, 2f, lineColor);

        float beatSec = 60f / Mathf.Max(1f, bpm);
        var weak = new Color(lineColor.r, lineColor.g, lineColor.b, lineColor.a * 0.45f);
        for (int k = 0; ; k++)
        {
            float sec = firstBeatOffset + k * beatSec;
            if (sec > totalSec) break;
            bool bar = beatsPerBar > 0 && k % beatsPerBar == 0;
            float x = sec * pixelsPerSecond;
            if (bar) AddLine(x - 1f, bottom - staffSpacing, 2f, staffSpacing * 6f, lineColor);
            else AddLine(x - 0.5f, bottom, 1f, staffSpacing * 4f, weak);
        }
    }

    void AddLine(float x, float y, float w, float h, Color c)
    {
        var r = NewRect("Line", linesRoot);
        r.anchoredPosition = new Vector2(x, y);
        r.sizeDelta = new Vector2(w, h);
        var img = r.gameObject.AddComponent<Image>();
        img.color = c;
        img.raycastTarget = false;
    }

    void UpdateView()
    {
        float bandPx = totalSec * pixelsPerSecond;
        float scale = fit ? Mathf.Min(1f, Screen.width / bandPx) : 1f;
        container.localScale = new Vector3(scale, scale, 1f);
        float leftPx = fit ? 0f : viewLeftSec * pixelsPerSecond;
        container.anchoredPosition = new Vector2(-leftPx * scale, (H - H * scale) * 0.5f);
        playhead.enabled = !fit;
    }

    // ---- モノクロと彩色 ----

    void ShowMonochrome()
    {
        LoadTakes();
        ResetPaper(SongLength);
        foreach (var ts in takes)
        {
            var mono = Mono(parts[ts.part].color);
            for (int i = 0; i < ts.take.samples.Count; i++)
                ProcessAt(ts.take, ts.part, i, mono);
            Flush();
        }
    }

    Color Mono(Color c)
    {
        float lum = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
        float v = Mathf.Lerp(0.25f, 0.6f, lum);
        return Color.Lerp(Color.white, new Color(v, v, v), monoStrength);
    }

    void ColorByTime(float from, float to)
    {
        foreach (var ts in takes)
        {
            var list = ts.take.samples;
            for (int i = 0; i < list.Count; i++)
            {
                float t = list[i].t;
                if (t <= from) continue;
                if (t > to) break;
                if (ts.colored[i]) continue;
                ProcessAt(ts.take, ts.part, i, parts[ts.part].color);
                ts.colored[i] = true;
            }
        }
    }

    void ColorByPosition(float from, float to)
    {
        foreach (var ts in takes)
        {
            var list = ts.take.samples;
            for (int i = 0; i < list.Count; i++)
            {
                if (ts.colored[i]) continue;
                float s = list[i].sec;
                if (s > from && s <= to)
                {
                    ProcessAt(ts.take, ts.part, i, parts[ts.part].color);
                    ts.colored[i] = true;
                }
            }
        }
    }

    // ---- 毎フレーム ----

    void Update()
    {
        stampMat.SetFloat("_Granulation", granulation);
        HandleKeys();
        ApplyMuteSolo();

        if (running)
        {
            float now = (float)(AudioSettings.dspTime - startDsp);
            viewLeftSec = now - PlayheadOffsetSec;
            if (now >= 0f)
            {
                ColorByTime(lastColorT, now);
                lastColorT = now;
            }

            if (recording && now >= 0f)
            {
                ReadPen(out Vector2 pos, out float p, out bool down);
                if (OverPanel(pos)) down = false;
                liveTake.samples.Add(new ScrollSample
                {
                    t = now,
                    sec = viewLeftSec + pos.x / pixelsPerSecond,
                    y = pos.y / H,
                    p = p,
                    down = down
                });
                ProcessAt(liveTake, livePart, liveTake.samples.Count - 1, parts[livePart].color);
            }

            if (now > SongLength + 0.5f) StopSession(recording);
        }
        else
        {
            HandleReading();
            if (!reading) HandleReviewInput();
        }

        Flush();
        UpdateView();
    }

    bool OverPanel(Vector2 bottomLeftPos)
    {
        return showHud && panelRect.Contains(new Vector2(bottomLeftPos.x, Screen.height - bottomLeftPos.y));
    }

    void HandleKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.rKey.wasPressedThisFrame && !running) StartSession(true);
        if (kb.pKey.wasPressedThisFrame && !running) StartSession(false);
        if (kb.spaceKey.wasPressedThisFrame && running) StopSession(true);
        if (kb.escapeKey.wasPressedThisFrame && running) StopSession(false);
        if (kb.hKey.wasPressedThisFrame) showHud = !showHud;
        if (kb.mKey.wasPressedThisFrame) parts[currentPart].mute = !parts[currentPart].mute;
        if (kb.sKey.wasPressedThisFrame) parts[currentPart].solo = !parts[currentPart].solo;
        if (running) return;
        if (kb.cKey.wasPressedThisFrame && !reading) ShowMonochrome();
        if (kb.fKey.wasPressedThisFrame) fit = !fit;
        if (kb.homeKey.wasPressedThisFrame) { fit = false; viewLeftSec = -PlayheadOffsetSec; }
        for (int i = 0; i < digitKeys.Length && i < parts.Count; i++)
            if (kb[digitKeys[i]].wasPressedThisFrame) currentPart = i;
    }

    // ---- 読むように聴く（A/D） ----

    void HandleReading()
    {
        var kb = Keyboard.current;
        if (kb == null || Master == null) return;

        int dir = 0;
        if (kb.dKey.isPressed && !kb.aKey.isPressed) dir = 1;
        if (kb.aKey.isPressed && !kb.dKey.isPressed) dir = -1;
        float speed = kb.shiftKey.isPressed ? 0.5f : 1f;

        if (dir == 0)
        {
            if (reading) StopReading();
            return;
        }

        fit = false;
        float len = Master.clip.length;
        float pos = viewLeftSec + PlayheadOffsetSec;
        if ((dir > 0 && pos >= len - 0.02f) || (dir < 0 && pos <= 0.02f))
        {
            if (reading) StopReading();
            return;
        }

        if (!reading || dir != readDir)
        {
            StartAllAt(pos, dir * speed);
            reading = true;
            readDir = dir;
        }
        foreach (var r in rts) r.src.pitch = dir * speed;

        float newPos = Master.time;
        if (dir > 0 && newPos > pos) ColorByPosition(pos, newPos);
        viewLeftSec = newPos - PlayheadOffsetSec;
    }

    // 全パートを同じ位置から同時に鳴らす
    void StartAllAt(float pos, float pitch)
    {
        double at = AudioSettings.dspTime + 0.05;
        foreach (var r in rts)
        {
            var s = r.src;
            s.Stop();
            if (s.clip == null) continue;
            float len = s.clip.length;
            if (pos > len - 0.02f) continue;
            s.pitch = pitch;
            s.time = Mathf.Clamp(pos, 0.02f, len - 0.02f);
            s.PlayScheduled(at);
        }
    }

    void StopAll()
    {
        foreach (var r in rts)
        {
            r.src.Stop();
            r.src.pitch = 1f;
        }
    }

    void StopReading()
    {
        StopAll();
        reading = false;
    }

    void HandleReviewInput()
    {
        if (fit) return;
        float dx = 0f;
        var pen = Pen.current;
        var m = Mouse.current;
        if (pen != null && pen.tip.isPressed && !OverPanel(pen.position.ReadValue())) dx = pen.delta.ReadValue().x;
        else if (m != null && m.leftButton.isPressed && !OverPanel(m.position.ReadValue())) dx = m.delta.ReadValue().x;
        if (m != null) dx += m.scroll.ReadValue().y * 0.5f;
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.leftArrowKey.isPressed) dx += 900f * Time.deltaTime;
            if (kb.rightArrowKey.isPressed) dx -= 900f * Time.deltaTime;
        }
        viewLeftSec = Mathf.Clamp(viewLeftSec - dx / pixelsPerSecond, -PlayheadOffsetSec, totalSec);
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
        if (Master == null) { Debug.LogError("どこかのパートに音源（Clip）を設定してください。"); return; }
        if (reading) StopReading();
        StopAll();

        ShowMonochrome();
        lastColorT = -1f;
        recording = record;
        livePart = currentPart;
        liveTake = new ScrollTake
        {
            part = parts[livePart].name,
            color = parts[livePart].color,
            seed = UnityEngine.Random.Range(1, 100000)
        };
        fit = false;
        startDsp = AudioSettings.dspTime + 0.3;
        foreach (var r in rts)
        {
            if (r.src.clip == null) continue;
            r.src.time = 0f;
            r.src.PlayScheduled(startDsp);
        }
        running = true;
    }

    void StopSession(bool save)
    {
        StopAll();
        if (save && recording && liveTake.samples.Count > 0) SaveTake(liveTake);
        running = false;
        recording = false;
        float keep = viewLeftSec;
        ShowMonochrome();
        viewLeftSec = keep;
    }

    void SaveTake(ScrollTake take)
    {
        Directory.CreateDirectory(TakesDir);
        string path = Path.Combine(TakesDir, "take_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(take));
        Debug.Log("保存しました（" + take.part + "）: " + path);
    }

    void LoadTakes()
    {
        takes.Clear();
        if (!Directory.Exists(TakesDir)) return;
        var files = Directory.GetFiles(TakesDir, "*.json");
        Array.Sort(files);
        foreach (var f in files)
        {
            var take = JsonUtility.FromJson<ScrollTake>(File.ReadAllText(f));
            if (take == null || take.samples == null) continue;
            int part = parts.FindIndex(p => p.name == take.part);
            if (part < 0)
            {
                Debug.LogWarning("パート「" + take.part + "」が見つからないので、" + parts[0].name + " として表示します: " + Path.GetFileName(f));
                part = 0;
            }
            takes.Add(new TakeState { take = take, part = part, colored = new bool[take.samples.Count] });
        }
    }

    // ---- 筆 ----

    Vector2 PosOf(ScrollSample s) => new Vector2(s.sec * pixelsPerSecond, s.y * H);

    // 乱数は記録点の番号から決まるので、モノクロと彩色の筆跡がぴったり重なる
    void ProcessAt(ScrollTake take, int part, int i, Color col)
    {
        var s = take.samples[i];
        if (!s.down) return;
        int c = take.seed * 1000003 + i * 4099;
        var b = PosOf(s);
        var prev = i > 0 ? take.samples[i - 1] : null;
        if (prev != null && prev.down)
        {
            Segment(ref c, part, PosOf(prev), prev.p, b, s.p, col);
        }
        else
        {
            Bloom(ref c, part, b, s.p, col);
            AddStamp(ref c, part, b, s.p, col);
        }
    }

    float Curve(float p) => Mathf.Pow(Mathf.Clamp01(p), pressureCurve);
    float Radius(float p) => Mathf.Lerp(minRadius, maxRadius, Curve(p)) * (H / 1080f);

    void Segment(ref int c, int part, Vector2 a, float pa, Vector2 b, float pb, Color col)
    {
        float dist = Vector2.Distance(a, b);
        float step = Mathf.Max(1f, Mathf.Min(Radius(pa), Radius(pb)) * spacing);
        int n = Mathf.Max(1, Mathf.CeilToInt(dist / step));
        for (int i = 1; i <= n; i++)
        {
            float k = (float)i / n;
            AddStamp(ref c, part, Vector2.Lerp(a, b, k), Mathf.Lerp(pa, pb, k), col);
        }
    }

    void AddStamp(ref int c, int part, Vector2 pos, float p, Color col)
    {
        float r = Radius(p) * Mathf.Lerp(0.85f, 1.15f, Hash(c++));
        var jitter = new Vector2(Hash(c++) - 0.5f, Hash(c++) - 0.5f) * r * 0.3f;
        pending.Add(new Stamp
        {
            pos = pos + jitter,
            r = r,
            op = Mathf.Lerp(minOpacity, maxOpacity, Curve(p)),
            ang = Hash(c++) * Mathf.PI * 2f,
            col = col,
            part = part
        });
    }

    void Bloom(ref int c, int part, Vector2 center, float p, Color col)
    {
        float baseR = Radius(p);
        for (int k = 0; k < bloomCount; k++)
        {
            float ang = (k + Hash(c++)) / bloomCount * Mathf.PI * 2f;
            float d = baseR * bloomSpread * (0.4f + 0.6f * Hash(c++));
            pending.Add(new Stamp
            {
                pos = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d,
                r = baseR * (0.6f + 0.8f * Hash(c++)),
                op = bloomOpacity * Mathf.Max(0.3f, Curve(p)),
                ang = Hash(c++) * Mathf.PI * 2f,
                col = col,
                part = part
            });
        }
    }

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

        foreach (var r in rts)
            foreach (var list in r.byTile) list.Clear();

        for (int s = 0; s < pending.Count; s++)
        {
            var r = rts[pending[s].part];
            int count = r.tiles.Count;
            float ext = pending[s].r * 1.42f;
            int a = Mathf.Max(Mathf.FloorToInt((pending[s].pos.x - ext) / TW), 0);
            int b = Mathf.Min(Mathf.FloorToInt((pending[s].pos.x + ext) / TW), count - 1);
            for (int i = a; i <= b; i++) r.byTile[i].Add(s);
        }

        var prevRT = RenderTexture.active;
        foreach (var r in rts)
        {
            for (int i = 0; i < r.tiles.Count; i++)
            {
                if (r.byTile[i].Count == 0) continue;
                RenderTexture.active = r.tiles[i];
                GL.PushMatrix();
                GL.LoadPixelMatrix(i * TW, (i + 1) * TW, 0, H);
                stampMat.SetPass(0);
                GL.Begin(GL.QUADS);
                foreach (int idx in r.byTile[i])
                {
                    var s = pending[idx];
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
            }
        }
        RenderTexture.active = prevRT;
        pending.Clear();
    }

    void Vertex(Vector2 v, float u, float w)
    {
        GL.MultiTexCoord2(0, u, w);
        GL.MultiTexCoord2(1, v.x / H, v.y / H);
        GL.Vertex3(v.x, v.y, 0);
    }

    // パートの層は白（＝何も描かれていない）で初期化し、紙の上に乗算で重ねる
    void ClearTiles()
    {
        var prev = RenderTexture.active;
        foreach (var r in rts)
            foreach (var t in r.tiles)
            {
                RenderTexture.active = t;
                GL.Clear(true, true, Color.white);
            }
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

    // ---- 操作表示とパートのパネル ----

    void OnGUI()
    {
        if (!showHud) { panelRect = Rect.zero; return; }
        if (hud == null)
        {
            hud = new GUIStyle(GUI.skin.label) { fontSize = 18 };
            hud.normal.textColor = new Color(0.25f, 0.25f, 0.25f);
            rowLabel = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            rowLabel.normal.textColor = Color.white;
        }

        string status;
        if (running)
        {
            float now = Mathf.Max(0f, (float)(AudioSettings.dspTime - startDsp));
            status = (recording ? "● 録音中（" + parts[livePart].name + "）  " : "▶ 再生中  ") + now.ToString("0.0") + "秒   Space:停止して保存  Esc:破棄";
        }
        else
        {
            string mode = reading ? (readDir > 0 ? "▶ 読み進め中  " : "◀ 読み返し中  ") : "";
            status = mode + "D:読み進める  A:読み返す  Shift:ゆっくり  R:録音  P:再生  1〜9:パート  M/S:ミュート/ソロ  C:モノクロ  F:全体  Home:先頭  H:隠す";
        }
        GUI.Label(new Rect(16, 12, 1900, 30), status, hud);

        const float rowH = 32f;
        panelRect = new Rect(16, 44, 370, 12 + parts.Count * rowH);
        GUI.Box(panelRect, GUIContent.none);

        for (int i = 0; i < parts.Count; i++)
        {
            float x = panelRect.x + 8, y = panelRect.y + 6 + i * rowH;

            GUI.enabled = !running;
            bool sel = GUI.Toggle(new Rect(x, y + 5, 22, 22), currentPart == i, GUIContent.none);
            if (sel && currentPart != i) currentPart = i;
            GUI.enabled = true;

            var old = GUI.color;
            GUI.color = Audible(i) ? parts[i].color : new Color(0.5f, 0.5f, 0.5f);
            GUI.DrawTexture(new Rect(x + 28, y + 6, 18, 18), Texture2D.whiteTexture);
            GUI.color = old;

            string label = (i + 1) + "  " + parts[i].name + (parts[i].clip == null ? "（音源なし）" : "");
            GUI.Label(new Rect(x + 54, y + 3, 200, 26), label, rowLabel);

            parts[i].mute = GUI.Toggle(new Rect(x + 262, y + 2, 42, 26), parts[i].mute, "M", "Button");
            parts[i].solo = GUI.Toggle(new Rect(x + 310, y + 2, 42, 26), parts[i].solo, "S", "Button");
        }
    }
}