using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

// 詩集を読むように曲を聴くプレイヤー
// ・曲を「ページ」（既定は8小節）ごとに区切り、そのページの範囲を各ステムでループ再生する
// ・ページを送ると、いま流れているループが最後まで鳴り終わってから次のページに移る（ぷつっと切れない）
// ・ステムごとに、ページの範囲を拍単位でずらせる（アウフタクトなど）
// ・画面：上に手書き歌詞（透過PNG）、下に各段。音源の段は波形、MIDIの段はライン
// ・MIDIの段は「音がないページでは隠す」にでき、表示される段だけで詰めて並ぶ
// ・ミュートした段はグレーアウトする
//
// 操作（コントローラー／キーボード）
//   R / E             : 次のページ
//   L / Q             : 前のページ
//   十字キー上下 / ↑↓ : 段を選ぶ（隠れている段は飛ばす）
//   A / Space         : 選んだ段のミュート切り替え
//
// ※音源の Load Type は「Decompress On Load」にする（継ぎ目のずれ防止と、波形の読み取りのため）
// ※MIDIファイルは拡張子を .bytes に変えて Assets に入れる（例：song.mid → song.bytes）

[Serializable]
public class PoemStem
{
    public string name = "ステム";
    [Tooltip("この段で鳴らす音源（MIDIだけの段なら空でよい）")]
    public AudioClip clip;
    [Tooltip("この段にラインとして描くMIDI（拡張子を .bytes にしたもの）")]
    public TextAsset midi;
    [Tooltip("MIDIファイルの中のトラック名（DAWのトラック名）。空なら下の番号で選ぶ")]
    public string midiTrackName;
    [Tooltip("ノートがあるトラックの何番目か（1から）。0なら全トラックをまとめて使う")]
    public int midiTrackNumber;
    [Tooltip("音源を曲の途中から書き出した場合、その書き出しの頭が何小節目か（曲の頭から書き出したなら1）")]
    public int clipStartBar = 1;
    [Tooltip("MIDIを曲の途中から書き出した場合、その書き出しの頭が何小節目か（曲の頭から書き出したなら1）")]
    public int midiStartBar = 1;
    [Tooltip("そのページにノートがないとき、この段を隠す（ハモリ用）")]
    public bool hideWhenNoNotes;
    public bool mute;
}

[Serializable]
public class StemWindow
{
    [Tooltip("何番目の段か（1から数える）")]
    public int stemNumber = 1;
    [Tooltip("始まりをずらす拍数。-1 なら1拍前から鳴らす（アウフタクト）")]
    public float startOffsetBeats;
    [Tooltip("終わりをずらす拍数。-1 なら1拍早く止める")]
    public float endOffsetBeats;
}

[Serializable]
public class PoemPage
{
    public string title;
    [Tooltip("手書き歌詞の透過PNG")]
    public Texture2D lyricImage;
    [Tooltip("画像がないときに代わりに表示する文字")]
    [TextArea(2, 4)] public string lyric;
    [Tooltip("このページが始まる小節（1から数える）")]
    public int startBar = 1;
    public int lengthBars = 8;
    [Tooltip("このページだけ、特定の段の範囲をずらしたいときに追加する")]
    public List<StemWindow> stemWindows = new List<StemWindow>();
    [Tooltip("自動生成の代わりに使う画像（段の順番どおり）。書き出したPNGを加工したものを、縦横比を変えずに入れる")]
    public List<Texture2D> stemImageOverrides = new List<Texture2D>();
}

public class PoemPagePlayer : MonoBehaviour
{
    public enum ButtonLayout { Xbox, Nintendo }

    [Header("段（音源・MIDI）")]
    public List<PoemStem> stems = new List<PoemStem>
    {
        new PoemStem { name = "メロディ" },
        new PoemStem { name = "ハモリ1", hideWhenNoNotes = true },
        new PoemStem { name = "ハモリ2", hideWhenNoNotes = true },
        new PoemStem { name = "ハモリ3", hideWhenNoNotes = true },
        new PoemStem { name = "間奏", hideWhenNoNotes = true },
        new PoemStem { name = "ボーカル" },
        new PoemStem { name = "上物" },
        new PoemStem { name = "ハーモニー" },
        new PoemStem { name = "シンセ" },
        new PoemStem { name = "パーカッション" },
        new PoemStem { name = "SE" },
    };

    [Header("テンポと小節")]
    public float bpm = 126f;
    public int beatsPerBar = 4;
    [Tooltip("曲の頭から、1小節目の1拍目までの秒数（アウフタクトがあるならその長さ）")]
    public float firstBarOffsetSec = 0f;

    [Tooltip("テンポの変化を読み取るMIDI（DAWから書き出したもの）。空なら段のMIDIから自動で探す。見つからなければ上のBPMで一定")]
    public TextAsset tempoMidi;

    [Header("ページ（空のまま再生すると、8小節ごとに自動で作られる）")]
    public List<PoemPage> pages = new List<PoemPage>();

    [Header("MIDIライン")]
    [Tooltip("MIDIと音源の頭がずれているときの補正（秒）")]
    public float midiOffsetSec = 0f;
    [Tooltip("MIDIの段に音源もあるとき、うっすら波形を残す濃さ（0で消す）")]
    [Range(0f, 1f)] public float midiRowWaveformAlpha = 0.2f;

    [Header("見た目")]
    public Color paperColor = new Color(0.98f, 0.97f, 0.95f);
    public Color inkColor = new Color(0.12f, 0.12f, 0.13f);
    public Color mutedColor = new Color(0.80f, 0.80f, 0.80f);
    public Color playheadColor = new Color(0.85f, 0.35f, 0.35f, 0.7f);
    [Tooltip("段の名前などの文字のフォント（空なら標準）")]
    public Font labelFont;

    [Header("PNG書き出し（⋮メニュー →「ステム画像をPNGで書き出す」）")]
    public int exportWidth = 4096;
    public int exportHeight = 512;
    public Color exportColor = Color.black;

    [Header("確認用")]
    [Tooltip("画面右下に、音の時計と出力の状態を表示する（動作確認が終わったらオフに）")]
    public bool showAudioDebug = true;

    [Header("操作")]
    [Tooltip("Xbox系はAボタンが下、Switch系はAボタンが右")]
    public ButtonLayout buttonLayout = ButtonLayout.Xbox;
    [Tooltip("ページの区切りの何秒前までに押せば、その区切りで切り替わるか")]
    public float scheduleAhead = 0.3f;

    // ---- 画面のレイアウト（1920×1080 基準） ----
    const float LyricX = 210f, LyricY = 50f, LyricW = 1500f, LyricH = 380f;
    const float RowsX = 300f, RowsW = 1480f, RowsTop = 470f, RowsBottom = 1050f, MaxRowH = 92f, RowGap = 10f;
    int TexW = 1024, TexH = 128;
    int Thick => Mathf.Max(1, TexH / 128);

    class MidiNote { public double q0, q1; public float start, end; public int pitch; }
    class MidiTrack { public string name; public List<MidiNote> notes = new List<MidiNote>(); }
    class TempoEvent { public double q, secPerQ; }
    struct TempoSeg { public double q, sec, secPerQ; }

    AudioSource[,] srcs;
    int[] flip;
    int currentPage, upcomingPage, pendingPage = -1, selectedStem, shownPage = -1;
    double nextIterStart;
    bool nextDecided;
    float decisionLead;

    Texture2D[,] rowTex;
    bool[,] rowVisible;
    float[] axisStart, axisEnd;
    double[] axisQ0, axisQ1;   // 画面の横軸は「拍」で取る（テンポが揺れても小節が等間隔に並ぶ）
    readonly Dictionary<TextAsset, List<MidiTrack>> midiCache = new Dictionary<TextAsset, List<MidiTrack>>();
    readonly Dictionary<TextAsset, List<TempoEvent>> midiTempo = new Dictionary<TextAsset, List<TempoEvent>>();
    List<TempoSeg> tempoMap;
    bool tempoBuilt;

    Font font;
    RawImage lyricImg;
    Text lyricText, pageText, pendingText, debugText;
    RectTransform gridRoot;
    Image playhead;
    readonly List<RectTransform> rowRoots = new List<RectTransform>();
    readonly List<RawImage> waveImgs = new List<RawImage>();
    readonly List<Text> labels = new List<Text>();
    readonly List<Text> markers = new List<Text>();
    readonly List<Image> highlights = new List<Image>();

    float BeatSec => 60f / Mathf.Max(1f, bpm);
    // 小節・拍 → 曲の頭からの秒数。MIDIにテンポの変化があれば、それに沿って計算する
    float BeatTime(int bar, float beats) => firstBarOffsetSec + (float)QToSec((bar - 1) * (double)Mathf.Max(1, beatsPerBar) + beats);
    float BarTime(int bar) => BeatTime(bar, 0f);
    float PageLen(int i) => BarTime(pages[i].startBar + Mathf.Max(1, pages[i].lengthBars)) - BarTime(pages[i].startBar);

    bool ready;

    const string Version = "2026-11-23 確認用";

    IEnumerator Start()
    {
        Debug.Log("PoemPagePlayer 起動（版：" + Version + "）　段の数 " + stems.Count + "　ページ数 " + pages.Count);

        // 音のデータの読み込みが終わるまで待つ（Preload Audio Data がオフでも動くように）
        foreach (var st in stems)
            if (st.clip != null && st.clip.loadState != AudioDataLoadState.Loaded) st.clip.LoadAudioData();
        float waited = 0f;
        while (waited < 15f)
        {
            bool loading = false;
            foreach (var st in stems)
                if (st.clip != null && st.clip.loadState == AudioDataLoadState.Loading) loading = true;
            if (!loading) break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        foreach (var st in stems)
            if (st.clip != null && st.clip.loadState != AudioDataLoadState.Loaded)
                Debug.LogWarning("「" + st.name + "」の音源を読み込めませんでした（状態：" + st.clip.loadState + "）");

        tempoBuilt = false;
        BuildTempoMap(true);

        if (pages.Count == 0) GeneratePages();
        if (pages.Count == 0 || stems.Count == 0)
        {
            Debug.LogError("段に音源を入れて、ページを用意してください。");
            enabled = false;
            yield break;
        }
        FillEmptyBars(false);

        srcs = new AudioSource[stems.Count, 2];
        flip = new int[stems.Count];
        for (int s = 0; s < stems.Count; s++)
            for (int k = 0; k < 2; k++)
            {
                var a = gameObject.AddComponent<AudioSource>();
                a.playOnAwake = false;
                srcs[s, k] = a;
            }

        LogMidiTracks();
        BuildTextures();
        BuildUI();

        float preRoll = MaxPreRoll();
        decisionLead = scheduleAhead + preRoll;

        double first = AudioSettings.dspTime + 0.5 + preRoll;
        currentPage = 0;
        ScheduleIteration(0, first);
        nextIterStart = first + PageLen(0);
        ShowPage(0);
        ready = true;
    }

    // ---- 自動でページを作る ----

    [ContextMenu("ページを8小節ごとに自動作成")]
    void GeneratePages()
    {
        float len = 0f;
        foreach (var s in stems) if (s.clip != null) len = Mathf.Max(len, s.clip.length);
        if (len <= 0f)
        {
            Debug.LogWarning("段に音源を入れてから実行してください。");
            return;
        }
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "ページを自動作成");
#endif
        tempoBuilt = false;
        int bars = 0;
        while (bars < 10000 && BarTime(bars + 2) <= len + 0.001f) bars++;
        pages.Clear();
        for (int b = 1; b <= bars; b += 8)
            pages.Add(new PoemPage { title = "ページ" + (pages.Count + 1), startBar = b, lengthBars = Mathf.Min(8, bars - b + 1) });
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("小節が0のページを埋める（歌詞画像は残す）")]
    void FillEmptyBarsMenu() => FillEmptyBars(true);

    // Start Bar や Length Bars が 0 のページに、前のページの続きから8小節を割り当てる
    void FillEmptyBars(bool save)
    {
#if UNITY_EDITOR
        if (save) UnityEditor.Undo.RecordObject(this, "小節を埋める");
#endif
        int fixedCount = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            bool changed = false;
            if (p.lengthBars <= 0) { p.lengthBars = 8; changed = true; }
            if (p.startBar <= 0)
            {
                p.startBar = i == 0 ? 1 : pages[i - 1].startBar + pages[i - 1].lengthBars;
                changed = true;
            }
            if (changed) fixedCount++;
        }
        if (fixedCount > 0)
            Debug.LogWarning(fixedCount + "ページの Start Bar / Length Bars が0だったので、8小節ずつ自動で割り当てました。" +
                             (save ? "" : "　残すには、再生を止めてから ⋮ →「小節が0のページを埋める」を実行してください。"));
#if UNITY_EDITOR
        if (save) UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ---- 画像の書き出し ----

    [ContextMenu("ステム画像をPNGで書き出す")]
    void ExportPngs()
    {
        if (pages.Count == 0) GeneratePages();
        if (pages.Count == 0) return;

        tempoBuilt = false;
        int oldW = TexW, oldH = TexH;
        var oldTex = rowTex;
        TexW = Mathf.Max(64, exportWidth);
        TexH = Mathf.Max(16, exportHeight);
        BuildTextures();

        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ExportedStems"));
        Directory.CreateDirectory(dir);
        var ink = (Color32)exportColor;
        int count = 0;
        for (int p = 0; p < pages.Count; p++)
            for (int s = 0; s < stems.Count; s++)
            {
                var tex = rowTex[p, s];
                if (rowVisible[p, s])
                {
                    var px = tex.GetPixels32();
                    for (int i = 0; i < px.Length; i++)
                        px[i] = new Color32(ink.r, ink.g, ink.b, px[i].a);
                    tex.SetPixels32(px);
                    tex.Apply();
                    string file = string.Format("p{0:00}_s{1}_{2}.png", p + 1, s + 1, stems[s].name);
                    File.WriteAllBytes(Path.Combine(dir, file), tex.EncodeToPNG());
                    count++;
                }
                DestroyImmediate(tex);
            }

        TexW = oldW;
        TexH = oldH;
        rowTex = oldTex;
        Debug.Log(count + "枚書き出しました: " + dir);
    }

    [ContextMenu("MIDIのトラック一覧をConsoleに出す")]
    void LogMidiTracks()
    {
        var seen = new HashSet<TextAsset>();
        foreach (var st in stems)
        {
            if (st.midi == null || !seen.Add(st.midi)) continue;
            var tracks = GetTracks(st.midi);
            if (tracks == null) continue;
            var sb = new StringBuilder("MIDI「" + st.midi.name + "」のトラック（ノートがあるもの）:\n");
            for (int i = 0; i < tracks.Count; i++)
                sb.AppendLine("  " + (i + 1) + ": " + (string.IsNullOrEmpty(tracks[i].name) ? "(名前なし)" : tracks[i].name) + "　ノート数 " + tracks[i].notes.Count);
            Debug.Log(sb.ToString());
        }
    }

    // ---- 再生のしくみ ----

    float MaxPreRoll()
    {
        float m = 0f;
        foreach (var p in pages)
            foreach (var w in p.stemWindows)
                if (w.startOffsetBeats < 0f) m = Mathf.Max(m, BarTime(p.startBar) - BeatTime(p.startBar, w.startOffsetBeats));
        return m;
    }

    void GetWindow(PoemPage page, int stemIdx, out float ws, out float we)
    {
        int endBar = page.startBar + Mathf.Max(1, page.lengthBars);
        ws = BarTime(page.startBar);
        we = BarTime(endBar);
        foreach (var w in page.stemWindows)
        {
            if (w.stemNumber - 1 != stemIdx) continue;
            ws = BeatTime(page.startBar, w.startOffsetBeats);
            we = BeatTime(endBar, w.endOffsetBeats);
        }
    }

    // 「boundary」の時刻にページの1小節目が鳴るように、全段の音源を予約する。
    // 段ごとに2つの AudioSource を交互に使うので、アウフタクトが前のループの終わりと重なっても切れない。
    void ScheduleIteration(int pageIdx, double boundary)
    {
        var page = pages[pageIdx];
        float pageStart = BarTime(page.startBar);

        for (int s = 0; s < stems.Count; s++)
        {
            var clip = stems[s].clip;
            if (clip == null)
            {
                if (!loggedFirstSchedule) Debug.Log("予約「" + stems[s].name + "」 音源なしのためスキップ");
                continue;
            }

            GetWindow(page, s, out float ws, out float we);
            double dspStart = boundary + (ws - pageStart);
            // 曲の時刻 → この音源ファイルの中の時刻（途中から書き出した音源のぶんずらす）
            float off = ClipOffset(s);
            ws -= off;
            we -= off;
            if (ws < 0f) { dspStart += -ws; ws = 0f; }
            we = Mathf.Min(we, clip.length);
            if (we <= ws) continue;

            var a = srcs[s, flip[s]];
            flip[s] = 1 - flip[s];
            a.Stop();
            a.clip = clip;
            a.timeSamples = Mathf.Clamp((int)(ws * clip.frequency), 0, clip.samples - 1);
            a.mute = stems[s].mute;
            a.PlayScheduled(dspStart);
            a.SetScheduledEndTime(dspStart + (we - ws));

            if (!loggedFirstSchedule)
                Debug.Log("予約「" + stems[s].name + "」 " + ws.ToString("0.00") + "〜" + we.ToString("0.00") + "秒　開始まで " +
                          (dspStart - AudioSettings.dspTime).ToString("0.00") + "秒　読み込み " + clip.loadState +
                          "　isPlaying " + a.isPlaying + "　enabled " + a.isActiveAndEnabled);
        }
        loggedFirstSchedule = true;
    }

    bool loggedFirstSchedule;

    void Update()
    {
        if (!ready) return;
        HandleInput();

        for (int s = 0; s < stems.Count; s++)
        {
            srcs[s, 0].mute = stems[s].mute;
            srcs[s, 1].mute = stems[s].mute;
        }

        double now = AudioSettings.dspTime;

        if (!nextDecided && now >= nextIterStart - decisionLead)
        {
            upcomingPage = pendingPage >= 0 ? pendingPage : currentPage;
            pendingPage = -1;
            ScheduleIteration(upcomingPage, nextIterStart);
            nextDecided = true;
        }

        if (nextDecided && now >= nextIterStart)
        {
            currentPage = upcomingPage;
            nextIterStart += PageLen(currentPage);
            nextDecided = false;
        }

        if (currentPage != shownPage) ShowPage(currentPage);
        UpdateVisuals(now);
    }

    // ---- 操作 ----

    void HandleInput()
    {
        var gp = Gamepad.current;
        var kb = Keyboard.current;

        ButtonControl aBtn = null;
        if (gp != null) aBtn = buttonLayout == ButtonLayout.Xbox ? gp.buttonSouth : gp.buttonEast;

        bool next = (gp != null && gp.rightShoulder.wasPressedThisFrame) || (kb != null && kb.eKey.wasPressedThisFrame);
        bool prev = (gp != null && gp.leftShoulder.wasPressedThisFrame) || (kb != null && kb.qKey.wasPressedThisFrame);
        bool up = (gp != null && gp.dpad.up.wasPressedThisFrame) || (kb != null && kb.upArrowKey.wasPressedThisFrame);
        bool down = (gp != null && gp.dpad.down.wasPressedThisFrame) || (kb != null && kb.downArrowKey.wasPressedThisFrame);
        bool toggle = (aBtn != null && aBtn.wasPressedThisFrame) || (kb != null && kb.spaceKey.wasPressedThisFrame);

        if (next) RequestPage(+1);
        if (prev) RequestPage(-1);
        if (up) MoveSelection(-1);
        if (down) MoveSelection(+1);
        if (toggle && shownPage >= 0 && rowVisible[shownPage, selectedStem])
            stems[selectedStem].mute = !stems[selectedStem].mute;
    }

    // 表示されている段の中だけで選択を動かす
    void MoveSelection(int dir)
    {
        if (shownPage < 0) return;
        for (int i = 1; i <= stems.Count; i++)
        {
            int s = ((selectedStem + dir * i) % stems.Count + stems.Count) % stems.Count;
            if (rowVisible[shownPage, s]) { selectedStem = s; return; }
        }
    }

    void RequestPage(int delta)
    {
        int decided = nextDecided ? upcomingPage : currentPage;
        int basePage = pendingPage >= 0 ? pendingPage : decided;
        int target = Mathf.Clamp(basePage + delta, 0, pages.Count - 1);
        pendingPage = target == decided ? -1 : target;
    }

    // ---- 波形とMIDIラインの画像を作る（全ページぶん） ----

    List<MidiNote> GetNotes(PoemStem st)
    {
        var src = SelectNotes(st);
        if (src == null) return null;
        // 途中から書き出したMIDIは、その小節のぶん後ろにずらしてから秒に直す
        double offQ = (Mathf.Max(1, st.midiStartBar) - 1) * (double)Mathf.Max(1, beatsPerBar);
        var list = new List<MidiNote>(src.Count);
        foreach (var n in src)
            list.Add(new MidiNote
            {
                q0 = n.q0 + offQ,
                q1 = n.q1 + offQ,
                pitch = n.pitch,
                start = (float)QToSec(n.q0 + offQ) + midiOffsetSec,
                end = (float)QToSec(n.q1 + offQ) + midiOffsetSec
            });
        return list;
    }

    List<MidiNote> SelectNotes(PoemStem st)
    {
        if (st.midi == null) return null;
        var tracks = GetTracks(st.midi);
        if (tracks == null || tracks.Count == 0) return null;

        if (!string.IsNullOrEmpty(st.midiTrackName))
        {
            foreach (var t in tracks)
                if (t.name != null && t.name.Trim() == st.midiTrackName.Trim()) return t.notes;
            Debug.LogWarning("「" + st.name + "」：MIDIにトラック名「" + st.midiTrackName + "」が見つかりません。Consoleのトラック一覧を確認してください。");
            return null;
        }
        if (st.midiTrackNumber > 0)
        {
            if (st.midiTrackNumber <= tracks.Count) return tracks[st.midiTrackNumber - 1].notes;
            Debug.LogWarning("「" + st.name + "」：MIDIのトラック番号 " + st.midiTrackNumber + " がありません。");
            return null;
        }
        var all = new List<MidiNote>();
        foreach (var t in tracks) all.AddRange(t.notes);
        all.Sort((a, b) => a.q0.CompareTo(b.q0));
        return all;
    }

    // ---- テンポマップ（テンポが途中で変わる曲のため） ----

    void BuildTempoMap(bool log)
    {
        tempoBuilt = true;
        tempoMap = null;
        var candidates = new List<TextAsset>();
        if (tempoMidi != null) candidates.Add(tempoMidi);
        foreach (var st in stems) if (st.midi != null && !candidates.Contains(st.midi)) candidates.Add(st.midi);

        foreach (var c in candidates)
        {
            GetTracks(c);
            if (!midiTempo.TryGetValue(c, out var ev) || ev.Count == 0) continue;
            ev.Sort((a, b) => a.q.CompareTo(b.q));
            var map = new List<TempoSeg> { new TempoSeg { q = 0, sec = 0, secPerQ = ev[0].q <= 0.0001 ? ev[0].secPerQ : 60.0 / Mathf.Max(1f, bpm) } };
            foreach (var e in ev)
            {
                if (e.q <= 0.0001) continue;
                var last = map[map.Count - 1];
                map.Add(new TempoSeg { q = e.q, sec = last.sec + (e.q - last.q) * last.secPerQ, secPerQ = e.secPerQ });
            }
            tempoMap = map;
            if (log)
            {
                double minBpm = double.MaxValue, maxBpm = 0;
                foreach (var m in map) { double b = 60.0 / m.secPerQ; minBpm = Math.Min(minBpm, b); maxBpm = Math.Max(maxBpm, b); }
                Debug.Log("テンポを MIDI「" + c.name + "」から読み取りました：変化 " + map.Count + " 箇所、BPM " + minBpm.ToString("0.#") + "〜" + maxBpm.ToString("0.#"));
            }
            return;
        }
        if (log) Debug.Log("MIDIにテンポの情報が見つからなかったので、BPM " + bpm + " で一定として計算します。");
    }

    // 曲の頭からの秒数 → 四分音符いくつ目か（QToSec の逆）
    double SecToQ(double sec)
    {
        if (!tempoBuilt) BuildTempoMap(false);
        if (tempoMap == null) return sec / (60.0 / Mathf.Max(1f, bpm));
        var seg = tempoMap[0];
        for (int i = 1; i < tempoMap.Count && tempoMap[i].sec <= sec; i++) seg = tempoMap[i];
        return seg.q + (sec - seg.sec) / seg.secPerQ;
    }

    double SongSecToQ(double t) => SecToQ(t - firstBarOffsetSec);
    double QToSongSec(double q) => firstBarOffsetSec + QToSec(q);

    // 曲の時刻 t が、ページ p の横軸のどこ（0〜1）にあたるか
    float AxisFrac(float t, int p)
    {
        double span = axisQ1[p] - axisQ0[p];
        return span > 0 ? (float)((SongSecToQ(t) - axisQ0[p]) / span) : -1f;
    }

    // 四分音符いくつ目か → 曲の頭からの秒数
    double QToSec(double q)
    {
        if (!tempoBuilt) BuildTempoMap(false);
        if (tempoMap == null) return q * 60.0 / Mathf.Max(1f, bpm);
        var seg = tempoMap[0];
        for (int i = 1; i < tempoMap.Count && tempoMap[i].q <= q; i++) seg = tempoMap[i];
        return seg.sec + (q - seg.q) * seg.secPerQ;
    }

    List<MidiTrack> GetTracks(TextAsset asset)
    {
        if (midiCache.TryGetValue(asset, out var cached)) return cached;
        var tempos = new List<TempoEvent>();
        var parsed = ParseMidi(asset.bytes, tempos);
        midiTempo[asset] = tempos;
        if (parsed == null) Debug.LogWarning("MIDI「" + asset.name + "」を読み取れませんでした。");
        midiCache[asset] = parsed;
        return parsed;
    }

    static bool HasNotesIn(List<MidiNote> notes, float ws, float we)
    {
        foreach (var n in notes)
            if (n.end > ws && n.start < we) return true;
        return false;
    }

    void BuildTextures()
    {
        int P = pages.Count, S = stems.Count;
        var notes = new List<MidiNote>[S];
        for (int s = 0; s < S; s++) notes[s] = GetNotes(stems[s]);

        axisStart = new float[P];
        axisEnd = new float[P];
        axisQ0 = new double[P];
        axisQ1 = new double[P];
        rowVisible = new bool[P, S];
        var peaks = new float[P, S][];
        var stemMax = new float[S];

        for (int p = 0; p < P; p++)
        {
            float a0 = float.MaxValue, a1 = float.MinValue;
            for (int s = 0; s < S; s++)
            {
                GetWindow(pages[p], s, out float ws, out float we);
                a0 = Mathf.Min(a0, Mathf.Max(0f, ws));
                a1 = Mathf.Max(a1, we);

                bool hasMidi = notes[s] != null && notes[s].Count > 0;
                bool visible = stems[s].clip != null || hasMidi;
                if (visible && hasMidi && stems[s].hideWhenNoNotes && !HasNotesIn(notes[s], ws, we)) visible = false;
                rowVisible[p, s] = visible;
            }
            axisStart[p] = a0;
            axisEnd[p] = a1;
            axisQ0[p] = SongSecToQ(a0);
            axisQ1[p] = SongSecToQ(a1);

            for (int s = 0; s < S; s++)
            {
                peaks[p, s] = ComputePeaks(s, p, a0, a1);
                if (peaks[p, s] != null)
                    foreach (float v in peaks[p, s]) stemMax[s] = Mathf.Max(stemMax[s], v);
            }
        }

        rowTex = new Texture2D[P, S];
        for (int p = 0; p < P; p++)
        {
            // メロディとハモリの音の高さが比べられるよう、ページ内のMIDIの段は同じ音域の物差しで描く
            int lo = 127, hi = 0;
            for (int s = 0; s < S; s++)
            {
                if (notes[s] == null || !rowVisible[p, s]) continue;
                GetWindow(pages[p], s, out float ws, out float we);
                foreach (var n in notes[s])
                    if (n.end > ws && n.start < we) { lo = Mathf.Min(lo, n.pitch); hi = Mathf.Max(hi, n.pitch); }
            }
            if (hi < lo) { lo = 60; hi = 72; }
            if (hi - lo < 6) { int c = (lo + hi) / 2; lo = c - 3; hi = c + 3; }

            for (int s = 0; s < S; s++)
            {
                var px = new Color32[TexW * TexH];
                bool isMidiRow = notes[s] != null && notes[s].Count > 0;
                byte alpha = (byte)(255 * (isMidiRow ? midiRowWaveformAlpha : 1f));
                if (peaks[p, s] != null && stemMax[s] > 0f && alpha > 0)
                    DrawWaveform(px, peaks[p, s], stemMax[s], alpha);
                if (isMidiRow)
                {
                    GetWindow(pages[p], s, out float ws, out float we);
                    DrawMidi(px, notes[s], ws, we, axisStart[p], axisEnd[p], lo, hi);
                }
                var tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                tex.SetPixels32(px);
                tex.Apply();
                rowTex[p, s] = tex;
            }
        }
    }

    // 途中から書き出した音源の頭が、曲の何秒目にあたるか
    float ClipOffset(int s) => stems[s].clipStartBar > 1 ? BarTime(stems[s].clipStartBar) : 0f;

    float[] ComputePeaks(int s, int p, float a0, float a1)
    {
        var clip = stems[s].clip;
        if (clip == null) return null;
        GetWindow(pages[p], s, out float ws, out float we);
        float off = ClipOffset(s);
        ws = Mathf.Max(0f, ws - off);
        we = Mathf.Min(we - off, clip.length);
        if (we <= ws) return null;
        double qa0 = SongSecToQ(a0), qa1 = SongSecToQ(a1);

        int freq = clip.frequency, ch = clip.channels;
        int offset = (int)(ws * freq);
        int frames = Mathf.Min((int)((we - ws) * freq), clip.samples - offset);
        if (frames <= 0) return null;

        var buf = new float[frames * ch];
        if (!clip.GetData(buf, offset))
        {
            Debug.LogWarning("「" + stems[s].name + "」の波形を読めませんでした。Load Type を Decompress On Load にしてください。");
            return null;
        }

        var result = new float[TexW];
        for (int x = 0; x < TexW; x++)
        {
            // 横軸は拍。1列ぶんの拍の範囲を、音源ファイルの中の秒に直す
            float t0 = (float)QToSongSec(qa0 + (qa1 - qa0) * x / TexW) - off;
            float t1 = (float)QToSongSec(qa0 + (qa1 - qa0) * (x + 1) / TexW) - off;
            int f0 = Mathf.Clamp((int)((t0 - ws) * freq), 0, frames);
            int f1 = Mathf.Clamp((int)((t1 - ws) * freq), 0, frames);
            if (f1 <= f0) continue;
            int stride = Mathf.Max(1, (f1 - f0) / 256);
            float m = 0f;
            for (int f = f0; f < f1; f += stride)
                for (int c = 0; c < ch; c++)
                    m = Mathf.Max(m, Mathf.Abs(buf[f * ch + c]));
            result[x] = m;
        }
        return result;
    }

    void DrawWaveform(Color32[] px, float[] peaks, float max, byte alpha)
    {
        int mid = TexH / 2;
        for (int x = 0; x < TexW; x++)
        {
            int h = Mathf.RoundToInt(peaks[x] / max * (TexH / 2 - 6 * Thick));
            if (peaks[x] > 0f) h = Mathf.Max(h, 1);
            for (int y = mid - h; y <= mid + h; y++)
                px[y * TexW + x] = new Color32(255, 255, 255, alpha);
        }
    }

    void DrawMidi(Color32[] px, List<MidiNote> notes, float ws, float we, float a0, float a1, int lo, int hi)
    {
        float margin = TexH * 0.1f;
        double q0 = SongSecToQ(a0), q1 = SongSecToQ(a1);
        int X(float t) => Mathf.RoundToInt((float)((SongSecToQ(t) - q0) / (q1 - q0)) * (TexW - 1));
        int Y(int pitch) => Mathf.RoundToInt(margin + (float)(pitch - lo) / (hi - lo) * (TexH - margin * 2f));

        MidiNote prev = null;
        foreach (var n in notes)
        {
            if (n.end <= ws || n.start >= we) continue;
            float s = Mathf.Max(n.start, ws), e = Mathf.Min(n.end, we);
            if (prev != null && n.start - prev.end < BeatSec)
                ThickLine(px, X(Mathf.Min(prev.end, we)), Y(prev.pitch), X(s), Y(n.pitch), 2 * Thick);
            ThickLine(px, X(s), Y(n.pitch), X(e), Y(n.pitch), 4 * Thick);
            prev = n;
        }
    }

    void ThickLine(Color32[] px, int x0, int y0, int x1, int y1, int r)
    {
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), 1);
        for (int i = 0; i <= steps; i++)
        {
            int cx = Mathf.RoundToInt(Mathf.Lerp(x0, x1, (float)i / steps));
            int cy = Mathf.RoundToInt(Mathf.Lerp(y0, y1, (float)i / steps));
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= TexW || y < 0 || y >= TexH) continue;
                    px[y * TexW + x] = new Color32(255, 255, 255, 255);
                }
        }
    }

    // ---- MIDIファイルの読み取り（標準MIDIファイルの最小限の解析） ----

    List<MidiTrack> ParseMidi(byte[] d, List<TempoEvent> tempos)
    {
        try
        {
            if (d.Length < 14 || d[0] != 'M' || d[1] != 'T' || d[2] != 'h' || d[3] != 'd') return null;
            int hlen = ReadInt32(d, 4);
            int ntrks = (d[10] << 8) | d[11];
            int division = (d[12] << 8) | d[13];
            if ((division & 0x8000) != 0) return null; // SMPTE形式は非対応
            int pos = 8 + hlen;

            var tracks = new List<MidiTrack>();
            for (int tr = 0; tr < ntrks && pos + 8 <= d.Length; tr++)
            {
                int len = ReadInt32(d, pos + 4);
                bool isTrack = d[pos] == 'M' && d[pos + 1] == 'T' && d[pos + 2] == 'r' && d[pos + 3] == 'k';
                pos += 8;
                int end = Mathf.Min(pos + len, d.Length);
                if (!isTrack) { pos = end; continue; }

                var track = new MidiTrack();
                long tick = 0;
                int running = 0;
                var open = new Dictionary<int, long>();
                while (pos < end)
                {
                    tick += ReadVar(d, ref pos);
                    int status;
                    if ((d[pos] & 0x80) != 0) status = d[pos++];
                    else status = running;

                    if (status == 0xFF)
                    {
                        int type = d[pos++];
                        int mlen = (int)ReadVar(d, ref pos);
                        if (type == 0x03 && track.name == null) track.name = Encoding.UTF8.GetString(d, pos, mlen);
                        if (type == 0x51 && mlen == 3)
                        {
                            int usPerQ = (d[pos] << 16) | (d[pos + 1] << 8) | d[pos + 2];
                            tempos.Add(new TempoEvent { q = (double)tick / division, secPerQ = usPerQ / 1000000.0 });
                        }
                        pos += mlen;
                    }
                    else if (status == 0xF0 || status == 0xF7)
                    {
                        int slen = (int)ReadVar(d, ref pos);
                        pos += slen;
                    }
                    else
                    {
                        running = status;
                        int hiNib = status & 0xF0;
                        if (hiNib == 0x80 || hiNib == 0x90)
                        {
                            int note = d[pos++];
                            int vel = d[pos++];
                            int key = ((status & 0x0F) << 8) | note;
                            if (hiNib == 0x90 && vel > 0)
                            {
                                if (open.TryGetValue(key, out long st)) AddNote(track.notes, st, tick, note, division);
                                open[key] = tick;
                            }
                            else if (open.TryGetValue(key, out long st2))
                            {
                                AddNote(track.notes, st2, tick, note, division);
                                open.Remove(key);
                            }
                        }
                        else if (hiNib == 0xC0 || hiNib == 0xD0) pos += 1;
                        else pos += 2;
                    }
                }
                pos = end;
                if (track.notes.Count > 0)
                {
                    track.notes.Sort((a, b) => a.q0.CompareTo(b.q0));
                    tracks.Add(track);
                }
            }
            return tracks;
        }
        catch (Exception e)
        {
            Debug.LogWarning("MIDIの読み取りに失敗しました: " + e.Message);
            return null;
        }
    }

    void AddNote(List<MidiNote> notes, long startTick, long endTick, int pitch, int division)
    {
        if (endTick <= startTick) return;
        notes.Add(new MidiNote
        {
            q0 = (double)startTick / division,
            q1 = (double)endTick / division,
            pitch = pitch
        });
    }

    static int ReadInt32(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

    static long ReadVar(byte[] d, ref int pos)
    {
        long v = 0;
        byte b;
        do
        {
            b = d[pos++];
            v = (v << 7) | (long)(b & 0x7F);
        } while ((b & 0x80) != 0);
        return v;
    }

    // ---- 画面の組み立て ----

    RectTransform Place(string name, Transform parent, float x, float y, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    static void SetRect(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    Text MakeText(Transform parent, string name, float x, float y, float w, float h, int size, TextAnchor anchor)
    {
        var t = Place(name, parent, x, y, w, h).gameObject.AddComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = inkColor;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    Image MakeImage(Transform parent, string name, float x, float y, float w, float h, Color c)
    {
        var img = Place(name, parent, x, y, w, h).gameObject.AddComponent<Image>();
        img.color = c;
        img.raycastTarget = false;
        return img;
    }

    void BuildUI()
    {
        font = labelFont != null ? labelFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var cgo = new GameObject("PoemBook");
        var canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        var root = cgo.transform;

        var bg = Place("Paper", root, 0f, 0f, 0f, 0f);
        bg.anchorMin = Vector2.zero;
        bg.anchorMax = Vector2.one;
        bg.offsetMin = bg.offsetMax = Vector2.zero;
        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.color = paperColor;
        bgImg.raycastTarget = false;

        lyricImg = Place("Lyric", root, LyricX, LyricY, LyricW, LyricH).gameObject.AddComponent<RawImage>();
        lyricImg.raycastTarget = false;
        lyricText = MakeText(root, "LyricText", LyricX, LyricY, LyricW, LyricH, 52, TextAnchor.MiddleCenter);

        // 段の背景（選択中のハイライト）は小節線より下に置く
        var highlightRoot = Place("Highlights", root, 0f, 0f, 1920f, 1080f);
        gridRoot = Place("Grid", root, RowsX, RowsTop, RowsW, RowsBottom - RowsTop);

        for (int s = 0; s < stems.Count; s++)
        {
            highlights.Add(MakeImage(highlightRoot, "Highlight" + s, 0f, 0f, 10f, 10f, Color.clear));
            var row = Place("Row" + s, root, 0f, 0f, 1920f, MaxRowH);
            rowRoots.Add(row);
            markers.Add(MakeText(row, "Marker", 60f, 0f, 40f, MaxRowH, 26, TextAnchor.MiddleLeft));
            markers[s].text = "▶";
            labels.Add(MakeText(row, "Label", 100f, 0f, RowsX - 110f, MaxRowH, 26, TextAnchor.MiddleLeft));
            labels[s].text = stems[s].name;
            var w = Place("Wave", row, RowsX, 0f, RowsW, MaxRowH).gameObject.AddComponent<RawImage>();
            w.raycastTarget = false;
            waveImgs.Add(w);
        }

        playhead = MakeImage(root, "Playhead", RowsX, RowsTop - 6f, 2f, 10f, playheadColor);

        pageText = MakeText(root, "PageInfo", 40f, 20f, 900f, 36f, 24, TextAnchor.UpperLeft);
        pendingText = MakeText(root, "Pending", 40f, 56f, 900f, 36f, 22, TextAnchor.UpperLeft);
        debugText = MakeText(root, "AudioDebug", 1020f, 20f, 860f, 36f, 20, TextAnchor.UpperRight);
    }

    void ShowPage(int p)
    {
        shownPage = p;
        var page = pages[p];

        if (page.lyricImage != null)
        {
            float w = page.lyricImage.width, h = page.lyricImage.height;
            float k = Mathf.Min(LyricW / w, LyricH / h);
            SetRect(lyricImg.rectTransform, LyricX + (LyricW - w * k) * 0.5f, LyricY + (LyricH - h * k) * 0.5f, w * k, h * k);
            lyricImg.texture = page.lyricImage;
            lyricImg.enabled = true;
            lyricText.text = "";
        }
        else
        {
            lyricImg.enabled = false;
            lyricText.text = page.lyric;
        }

        // このページで見える段だけを、上から詰めて並べる
        var visible = new List<int>();
        for (int s = 0; s < stems.Count; s++)
        {
            bool on = rowVisible[p, s];
            rowRoots[s].gameObject.SetActive(on);
            highlights[s].gameObject.SetActive(on);
            if (on) visible.Add(s);
        }
        int n = Mathf.Max(1, visible.Count);
        float rowH = Mathf.Min(MaxRowH, (RowsBottom - RowsTop - RowGap * (n - 1)) / n);
        int fontSize = Mathf.RoundToInt(Mathf.Min(26f, rowH * 0.4f));
        for (int i = 0; i < visible.Count; i++)
        {
            int s = visible[i];
            float y = RowsTop + i * (rowH + RowGap);
            SetRect(rowRoots[s], 0f, y, 1920f, rowH);
            SetRect(markers[s].rectTransform, 60f, 0f, 40f, rowH);
            SetRect(labels[s].rectTransform, 100f, 0f, RowsX - 110f, rowH);
            SetRect(waveImgs[s].rectTransform, RowsX, 0f, RowsW, rowH);
            markers[s].fontSize = fontSize;
            labels[s].fontSize = fontSize;
            SetRect(highlights[s].rectTransform, 50f, y - 4f, RowsX + RowsW - 30f, rowH + 8f);

            Texture2D custom = s < page.stemImageOverrides.Count ? page.stemImageOverrides[s] : null;
            waveImgs[s].texture = custom != null ? custom : rowTex[p, s];
        }
        float rowsH = visible.Count * (rowH + RowGap) - RowGap;

        if (!rowVisible[p, selectedStem] && visible.Count > 0) selectedStem = visible[0];

        // 小節線
        for (int i = gridRoot.childCount - 1; i >= 0; i--) Destroy(gridRoot.GetChild(i).gameObject);
        SetRect(gridRoot, RowsX, RowsTop, RowsW, rowsH);
        float a0 = axisStart[p], a1 = axisEnd[p];
        var lineCol = new Color(inkColor.r, inkColor.g, inkColor.b, 0.12f);
        for (int b = 0; b <= page.lengthBars; b++)
        {
            float t = BarTime(page.startBar + b);
            if (t < a0 || t > a1) continue;
            float x = AxisFrac(t, p) * RowsW;
            MakeImage(gridRoot, "Bar", x - 1f, 0f, 2f, rowsH, lineCol);
        }
        playhead.rectTransform.sizeDelta = new Vector2(2f, rowsH + 12f);

        pageText.text = "ページ " + (p + 1) + " / " + pages.Count + "　" + page.title;
    }

    void UpdateVisuals(double now)
    {
        for (int s = 0; s < stems.Count; s++)
        {
            if (!rowVisible[shownPage, s]) continue;
            var c = stems[s].mute ? mutedColor : inkColor;
            waveImgs[s].color = c;
            labels[s].color = c;
            bool sel = s == selectedStem;
            markers[s].color = sel ? inkColor : Color.clear;
            highlights[s].color = sel ? new Color(inkColor.r, inkColor.g, inkColor.b, 0.05f) : Color.clear;
        }

        if (showAudioDebug)
        {
            int playing = 0;
            foreach (var a in srcs) if (a.isPlaying) playing++;
            debugText.text = "音の時計 " + now.ToString("0.00") + "秒　出力 " + AudioSettings.outputSampleRate + "Hz　再生中 " + playing;
        }
        else debugText.text = "";

        int waiting = pendingPage >= 0 ? pendingPage : (nextDecided && upcomingPage != currentPage ? upcomingPage : -1);
        pendingText.text = waiting >= 0 ? "→ ページ " + (waiting + 1) + " へ（このフレーズを読み終えてから）" : "";

        var page = pages[currentPage];
        double iterStart = nextIterStart - PageLen(currentPage);
        float songT = BarTime(page.startBar) + (float)(now - iterStart);
        float a0 = axisStart[currentPage], a1 = axisEnd[currentPage];
        float frac = AxisFrac(songT, currentPage);
        playhead.enabled = frac >= 0f && frac <= 1f;
        var rt = playhead.rectTransform;
        rt.anchoredPosition = new Vector2(RowsX + frac * RowsW - 1f, -(RowsTop - 6f));
    }
}