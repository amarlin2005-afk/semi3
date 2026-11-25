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
// ・曲を「ページ」ごとに区切り、そのページの範囲を各段（ステム）でループ再生する
// ・ページを送ると、いま流れているループが最後まで鳴り終わってから次のページに移る（ぷつっと切れない）
// ・ページの範囲も、段ごとの範囲（アウフタクトなど）も、拍単位で決められる
// ・画面：上に手書き歌詞（透過PNG）、下に各段。音源の段は波形、MIDIの段はライン
// ・横軸は「拍」。テンポが揺れても小節は等間隔に並び、赤い線の速さがテンポに合わせて変わる
// ・ミュートした段はグレーアウトする
//
// 操作（コントローラー／キーボード）
//   R / E             : 次のページ（いまのフレーズを読み終えてから）。コントローラーのLRは Controller Page Buttons をオンに
//   L / Q             : 前のページ
//   Auto Page Turn をオンにすると、回して区切りを越えたときに自動でめくれる
//   十字キー上下 / ↑↓ : 段を選ぶ（隠れている段は飛ばす）
//   A / Space         : 選んだ段のミュート切り替え
//   右スティック上下  : 段を選ぶ（倒しっぱなしで続けて動く）
//   右スティック押し込み : 選んだ段のミュート切り替え
//   Tab / Start       : 設定モードの切り替え
//   F1                : デバッグ用のページ一覧（押したページから再生）
//   F2                : 確認用の文字（音の時計・ページ番号・速さなど）の表示切り替え
//   左スティックを回す : 手回しで再生位置を動かす（時計回りで進む、反時計回りで戻る）。手を離すと少しして自動再生に戻る
//
// 設定モード（再生中にページの範囲・段ごとの範囲をマウスで調整する）
//   ・上の帯の両端をドラッグ      : ページ全体の範囲（ループの長さ）
//   ・各段の帯の両端をドラッグ    : その段だけ範囲をずらす（アウフタクトなど）
//   ・ふつうは1拍単位。Shift で半拍、Ctrl で 1/4拍
//   ・Delete                      : 選んでいる段のずらしを消す（ページの範囲に戻す）
//   ・変えた内容は再生を止めたときに自動でインスペクターに取り込まれる（そのあと Ctrl+S で保存）
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
    [Tooltip("ページの始まりからずらす拍数。-1 なら1拍前から鳴らす（アウフタクト）")]
    public float startOffsetBeats;
    [Tooltip("ページの終わりからずらす拍数。-1 なら1拍早く止める")]
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
    [Tooltip("ページの始まりを拍単位でずらす（2なら、その小節の3拍目から）")]
    public float startBeatOffset;
    [Tooltip("ページの終わりを拍単位でずらす（-2なら、最後の小節の3拍目まで）")]
    public float endBeatOffset;
    [Tooltip("このページだけ、特定の段の範囲をずらしたいときに追加する（設定モードで自動で作られる）")]
    public List<StemWindow> stemWindows = new List<StemWindow>();
    [Tooltip("自動生成の代わりに使う画像（段の順番どおり）。書き出したPNGを加工したものを、縦横比を変えずに入れる")]
    public List<Texture2D> stemImageOverrides = new List<Texture2D>();
    [Tooltip("このページで広がる水彩の色（透明のままなら、全体の Wash Color を使う）")]
    public Color washColor = new Color(0f, 0f, 0f, 0f);
}

public class PoemPagePlayer : MonoBehaviour
{
    public enum ButtonLayout { Xbox, Nintendo }
    public enum CrankMode { テンポ, 位置 }
    public enum AfterRelease { 巻き戻し, その場から自動再生 }

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
    [Tooltip("曲の頭から、1小節目の1拍目までの秒数（ふつうは0）")]
    public float firstBarOffsetSec = 0f;
    [Tooltip("テンポの変化を読み取るMIDI（DAWから「名前を付けて保存」でMIDI形式にしたもの）。空なら段のMIDIから自動で探す。見つからなければ上のBPMで一定")]
    public TextAsset tempoMidi;

    [Header("ページの小節数を一括で指定（⋮メニュー →「小節数の一覧をページに反映」）")]
    [Tooltip("1ページ目が始まる小節")]
    public int firstPageStartBar = 1;
    [Tooltip("各ページの小節数を、カンマかスペース区切りで順番に（例：4, 8, 8, 6, 8）。⋮ →「今のページの小節数を一覧に書き出す」で今の値が入る")]
    [TextArea(2, 4)] public string pageBarsList = "";
    [Tooltip("反映するときに、ページの始まり・終わりの拍のずれ（Start/End Beat Offset）を0に戻す")]
    public bool resetBeatOffsetsOnApply = true;

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
    [Tooltip("設定モードの範囲の色")]
    public Color editColor = new Color(0.20f, 0.45f, 0.85f, 1f);
    [Tooltip("段の名前などの文字のフォント（空なら標準）")]
    public Font labelFont;

    [Header("歌詞")]
    [Tooltip("歌詞の画像の大きさ（枠いっぱいが1）")]
    [Range(0.2f, 1f)] public float lyricScale = 0.65f;
    [Tooltip("手回しのテンポが1.00倍のときだけピントが合う（外れるほどぼける）")]
    public bool lyricFocus = true;
    [Tooltip("いちばんぼけたときのぼかしの大きさ（画面上のピクセル）")]
    [Range(0f, 40f)] public float maxBlurPx = 14f;
    [Tooltip("テンポが何倍ずれたら、いちばんぼけるか（0.5なら、0.5倍や1.5倍で最大）")]
    [Range(0.1f, 1f)] public float blurAtDeviation = 0.5f;
    [Tooltip("ピントが動く速さ（小さいほどゆっくり合う）")]
    [Range(0.5f, 20f)] public float focusSpeed = 5f;

    [Header("ループの余韻（区切りで音がぶつっと切れないように、残響をふわっと残す）")]
    public bool loopTail = true;
    [Tooltip("余韻を最初のページ（待機中に繰り返すページ）だけにする")]
    public bool tailOnlyFirstPage = true;
    [Tooltip("余韻が消えるまでの秒数")]
    [Range(0.3f, 5f)] public float tailSeconds = 2f;
    [Tooltip("余韻の大きさ")]
    [Range(0f, 1.5f)] public float tailLevel = 0.6f;
    [Tooltip("余韻のこもり具合（大きいほど柔らかく、高音が早く消える）")]
    [Range(0f, 0.9f)] public float tailDamping = 0.4f;

    [Header("左スティックで手回し")]
    public bool stickScrub = true;
    [Tooltip("テンポ：回すテンポ（周期）で再生のテンポが決まる。元のテンポに近いとぴったりに吸い付く\n位置：回した角度のぶんだけ、その場で進む・戻る")]
    public CrankMode crankMode = CrankMode.テンポ;
    [Tooltip("テンポモードで、スティックが一番上・一番下を通った間隔を、直近何回ぶん平均するか")]
    [Range(1, 8)] public int tempoAverageCount = 4;
    [Tooltip("テンポモードで、再生テンポが回すテンポに追いつく速さ（小さいほど揺れにくく、ゆったり変わる）")]
    [Range(0.2f, 10f)] public float tempoFollow = 2f;
    [Tooltip("再生の速さを、この刻みで段階的に変える（0.1なら 0.9倍・1.0倍・1.1倍…）。細かく揺れずに、曲として聴きやすくなる")]
    [Range(0.01f, 0.5f)] public float speedStep = 0.1f;
    [Tooltip("元のテンポの±何割以内なら、ぴったり1倍に吸い付けるか")]
    [Range(0f, 0.3f)] public float lockRange = 0.12f;
    [Tooltip("一度1倍に吸い付いたら、元のテンポの±何割ずれるまで離れないか（大きいほど1倍から動きにくい）")]
    [Range(0f, 0.8f)] public float lockHoldRange = 0.3f;
    [Tooltip("この範囲（±何割）の中で回し続けていれば、少しずれていても1倍に吸い付く")]
    [Range(0f, 0.8f)] public float lockAttractRange = 0.35f;
    [Tooltip("上の範囲で何秒回し続けたら、1倍に吸い付くか")]
    [Range(0.1f, 5f)] public float lockAttractSec = 1f;
    [Tooltip("拍ごとにコントローラーを振動させる（小節の頭は強め）")]
    public bool beatRumble = true;
    [Tooltip("スティックを1周回すと何拍進むか（4なら1周で1小節）")]
    public float beatsPerTurn = 4f;
    [Tooltip("スティックから手を離して何秒たったら、巻き戻し（または自動再生）に移るか")]
    public float resumeAfterSec = 1.5f;
    [Header("手を離したあと・展示用")]
    [Tooltip("巻き戻し：曲の最初まで少しずつ巻き戻って、最初のページの繰り返しに戻る\nその場から自動再生：止めた位置から続けて自動で流れる")]
    public AfterRelease afterRelease = AfterRelease.巻き戻し;
    [Tooltip("巻き戻しはじめの速さ（元のテンポの何倍か）")]
    [Range(0.1f, 4f)] public float rewindStartSpeed = 0.5f;
    [Tooltip("巻き戻しのいちばん速いときの速さ（元のテンポの何倍か）")]
    [Range(0.5f, 64f)] public float rewindMaxSpeed = 16f;
    [Tooltip("いちばん速くなるまでの秒数")]
    [Range(0.5f, 30f)] public float rewindAccelSec = 3f;
    [Tooltip("巻き戻し中の音の大きさ（0で無音）")]
    [Range(0f, 1f)] public float rewindVolume = 0.35f;
    [Tooltip("何も触られないまま何秒たったら、最初のページへ巻き戻すか（0で使わない）")]
    public float idleReturnSec = 60f;
    [Tooltip("最初のページに戻ったとき、ミュートを始めの状態（インスペクターの設定）に戻す")]
    public bool resetMutesOnReturn = true;
    [Tooltip("何も操作されないまま何秒たったら、ミュートを始めの状態（インスペクターの設定）に戻すか。ページはそのまま（0で使わない）")]
    public float idleMuteResetSec = 30f;


    [Tooltip("回す速さの変化を、どれくらいなめらかにするか（大きいほど機敏）")]
    public float scrubResponse = 10f;

    [Header("画面のUI画像（Illustratorのアートボード 1920×1080 のまま書き出した透過PNG）")]
    [Tooltip("動かない部分（紙・枠・スティックの土台・メーターの目盛り・LRボタンなど）")]
    public Texture2D uiBackground;
    [Tooltip("スティックの上の部分だけの画像。スティックの入力に合わせて動く")]
    public Texture2D stickTopImage;
    [Tooltip("スティックの上の部分を置く位置（画像の中心が、1920×1080 の画面のどこに来るか。左上が0,0）。画像を切り抜いて書き出した場合に使う")]
    public Vector2 stickTopCenter = new Vector2(340f, 195f);
    [Tooltip("スティックの上の部分の大きさ（書き出した画像の何倍で表示するか）")]
    public float stickTopScale = 1f;
    [Tooltip("スティックをいっぱいに倒したとき、上の部分が動く距離（ピクセル）")]
    public float stickTravel = 22f;
    [Tooltip("スティックの絵が入力についてくる速さ")]
    [Range(1f, 40f)] public float stickFollow = 18f;
    [Tooltip("メーターの針だけの画像")]
    public Texture2D needleImage;
    [Tooltip("針の根もと（回転の中心）を置く位置（1920×1080 の画面での位置。左上が0,0）")]
    public Vector2 needlePivot = new Vector2(1557f, 330f);
    [Tooltip("針の画像の中で、根もとがどこにあるか（0〜1。左下が0,0、右上が1,1。下の真ん中なら 0.5, 0）。画像を切り抜いて書き出した場合に使う")]
    public Vector2 needleRootInImage = new Vector2(0.5f, 0.05f);
    [Tooltip("針の大きさ（書き出した画像の何倍で表示するか）")]
    public float needleScale = 1f;
    [Tooltip("針がいちばん左（速さ0）・いちばん右に振れたときの角度（12時からの角度）")]
    [Range(10f, 170f)] public float needleMaxAngle = 75f;
    [Tooltip("何倍の速さで針がいちばん右に振れるか")]
    [Range(1.2f, 4f)] public float meterMaxRatio = 2f;
    [Tooltip("針のばねの硬さ（大きいほど機敏、小さいほどゆらゆら）")]
    [Range(5f, 200f)] public float needleStiffness = 60f;
    [Tooltip("針の揺れのおさまりやすさ（小さいほど行き過ぎて揺れる）")]
    [Range(0.1f, 2f)] public float needleDamping = 0.6f;
    [Tooltip("絵の具の模様の画像（グレースケール、透過PNG）。複数あってもよい。uiBackground には含めないこと")]
    public List<Texture2D> paintImages = new List<Texture2D>();
    [Tooltip("切り抜いて書き出した絵の具の模様を置く位置（画像の中心。Paint Images と同じ順番）")]
    public List<Vector2> paintCenters = new List<Vector2> { new Vector2(350f, 250f), new Vector2(1545f, 265f) };
    [Tooltip("絵の具の模様の大きさ（書き出した画像の何倍で表示するか）")]
    public float paintScale = 1f;
    [Tooltip("ハーモニーが鳴っているときに、絵の具の模様に付く色（ページごとの Wash Color があればそちらを優先）")]
    public Color paintColor = new Color(0.55f, 0.70f, 0.90f, 1f);
    [Tooltip("色の濃さ")]
    [Range(0.5f, 6f)] public float paintColorGain = 2.5f;
    [Tooltip("色が広がる・引くのにかかる秒数")]
    [Range(0.2f, 10f)] public float paintFadeSec = 2f;
    [Tooltip("いつも少しだけにじんで動く強さ（0で止まる）")]
    [Range(0f, 3f)] public float paintWobble = 1f;
    [Tooltip("Rボタンの中心（1920×1080 の画面での位置）")]
    public Vector2 rButtonCenter = new Vector2(968f, 398f);
    [Tooltip("Lボタンの中心")]
    public Vector2 lButtonCenter = new Vector2(865f, 398f);
    [Tooltip("ボタンの光の大きさ（ピクセル）")]
    public float buttonGlowSize = 150f;
    public Color buttonGlowColor = new Color(1f, 0.45f, 0.08f, 1f);
    [Tooltip("ボタンを押して、ページめくりが予約されたときの光の色")]
    public Color buttonPressedColor = new Color(0.25f, 0.85f, 0.35f, 1f);
    [Tooltip("左上のページ番号を表示する")]
    public bool showPageNumber = true;
    [Tooltip("この段をミュートすると、歌詞がぼける（段の名前）")]
    public string lyricStemName = "メロディ";

    [Header("水彩の色（指定の段が鳴っている間だけ、画面に色がにじんで広がる）")]
    public bool washEnabled = true;
    [Tooltip("どの段が鳴っているときに色を付けるか（段の名前）")]
    public string washStemName = "ハーモニー";
    [Tooltip("水彩のパターン（グレースケール。白＝紙のまま、黒いほど濃い）。色が付くたびに順番に使う。空なら自動で作った模様を使う")]
    public List<Texture2D> washPatterns = new List<Texture2D>();
    [Tooltip("広がる色（ページごとに変えたいときは、各ページの Wash Color で上書きできる）")]
    public Color washColor = new Color(0.55f, 0.70f, 0.90f, 1f);
    [Tooltip("色の濃さ")]
    [Range(0f, 1f)] public float washStrength = 0.55f;
    [Tooltip("にじみが画面全体に広がるまでの秒数")]
    [Range(0.3f, 10f)] public float washInSec = 3f;
    [Tooltip("段がミュートされてから、色が乾いて消えるまでの秒数")]
    [Range(0.2f, 10f)] public float washOutSec = 2.5f;
    [Tooltip("にじみの縁が濃くなる強さ（水彩らしさ）")]
    [Range(0f, 1f)] public float washEdge = 0.6f;
    [Tooltip("ページが変わって色が変わるとき、色が移り変わる秒数")]
    [Range(0.1f, 10f)] public float washColorChangeSec = 2f;

    [Header("ページめくりのアニメーション")]
    public bool pageTurnAnim = true;
    [Tooltip("1回めくるのにかかる秒数")]
    [Range(0.2f, 3f)] public float pageTurnSec = 0.9f;
    [Tooltip("巻き戻し中は、何倍の速さでめくるか（ぱらぱらとめくれる）")]
    [Range(1f, 10f)] public float rewindTurnSpeedUp = 4f;
    [Tooltip("めくれる紙の影の濃さ")]
    [Range(0f, 1f)] public float pageTurnShadow = 0.35f;
    [Tooltip("紙の裏に、表の文字がうっすら透ける強さ")]
    [Range(0f, 0.5f)] public float pageShowThrough = 0.12f;

    [Header("設定モード")]
    [Tooltip("ドラッグしたときに吸い付く間隔（拍）。Shiftで半分、Ctrlで1/4")]
    public float snapBeats = 1f;

    [Header("PNG書き出し（⋮メニュー →「ステム画像をPNGで書き出す」）")]
    public int exportWidth = 4096;
    public int exportHeight = 512;
    public Color exportColor = Color.black;

    [Header("デバッグ")]
    [Tooltip("再生を始めたときに、このページから始める（1から数える。0ならふつうに最初のページの待機から）")]
    public int debugStartPage = 0;
    [Tooltip("デバッグで飛んだページを繰り返し再生する（オフなら、そこから自動で次のページへ進む）")]
    public bool debugLoopPage = true;
    [Tooltip("F1 で開くページ一覧を、最初から開いておく")]
    public bool showDebugPanel = false;

    [Header("再生中に変えた設定")]
    [Tooltip("再生中にインスペクターで変えた値（テンポ・余韻・巻き戻しなどの設定）を、止めたあとも残す。段のミュートは残さない")]
    public bool keepPlayModeTweaks = true;

    [Header("確認用")]
    [Tooltip("確認用の文字（音の時計・ページ番号・ページ予約・速さ「ぴったり」など）をまとめて表示する。再生中は F2 でも切り替えられる。展示ではオフに")]
    public bool showAudioDebug = true;

    [Header("操作")]
    [Tooltip("コントローラーのLRでページをめくれるようにする")]
    public bool controllerPageButtons = false;
    [Tooltip("オン：区切りを越えると自動で次のページへ\nオフ：LR（またはE/Q）でめくるまで、同じページを繰り返す")]
    public bool autoPageTurn = false;
    [Tooltip("Xbox系はAボタンが下、Switch系はAボタンが右")]
    public ButtonLayout buttonLayout = ButtonLayout.Xbox;
    [Tooltip("ページの区切りの何秒前までに押せば、その区切りで切り替わるか")]
    public float scheduleAhead = 0.3f;

    // ---- 画面のレイアウト（1920×1080 基準） ----
    const float LyricX = 210f, LyricY = 50f, LyricW = 1500f, LyricH = 380f;
    const float RowsX = 300f, RowsW = 1480f, RowsTop = 470f, RowsBottom = 1040f, MaxRowH = 92f, RowGap = 10f;
    const float PageRowY = 434f, PageRowH = 28f, HandleW = 10f;
    int TexW = 1024, TexH = 128;
    int Thick => Mathf.Max(1, TexH / 128);

    class MidiNote { public double q0, q1; public float start, end; public int pitch; }
    class MidiTrack { public string name; public List<MidiNote> notes = new List<MidiNote>(); }
    class TempoEvent { public double q, secPerQ; }
    struct TempoSeg { public double q, sec, secPerQ; }

    [Serializable] class PageTiming { public int startBar, lengthBars; public float startBeatOffset, endBeatOffset; public List<StemWindow> stemWindows = new List<StemWindow>(); }
    [Serializable] class PageTimingFile { public List<PageTiming> pages = new List<PageTiming>(); }

    // 再生
    AudioSource[,] srcs, tailSrcs;
    int[] flip, tailFlip;
    readonly Dictionary<long, AudioClip> tailCache = new Dictionary<long, AudioClip>();
    int currentPage, upcomingPage, pendingPage = -1, selectedStem, shownPage = -1;
    double nextIterStart;
    bool nextDecided, ready, loggedFirstSchedule;
    float decisionLead;

    // 画像
    Texture2D[,] rowTex;
    bool[,] rowVisible;
    bool[] texEdit;
    double[] axisQ0, axisQ1;   // 画面の横軸は「拍」（四分音符いくつ目か）
    float[] stemMax;
    List<MidiNote>[] notesCache;

    // MIDIとテンポ
    readonly Dictionary<TextAsset, List<MidiTrack>> midiCache = new Dictionary<TextAsset, List<MidiTrack>>();
    readonly Dictionary<TextAsset, List<TempoEvent>> midiTempo = new Dictionary<TextAsset, List<TempoEvent>>();
    List<TempoSeg> tempoMap;
    bool tempoBuilt;

    // UI
    Font font;
    Canvas uiCanvas;
    RawImage lyricImg;
    Text lyricText, pageText, pendingText, debugText;
    RectTransform gridRoot;
    Image playhead;
    readonly List<RectTransform> rowRoots = new List<RectTransform>();
    readonly List<RawImage> waveImgs = new List<RawImage>();
    readonly List<Text> labels = new List<Text>();
    readonly List<Text> markers = new List<Text>();
    readonly List<Image> highlights = new List<Image>();
    float[] rowY;
    float curRowH = MaxRowH;

    // 設定モード
    bool editMode;
    RectTransform editRoot;
    Image pageBand, pageH0, pageH1;
    Text pageRangeText, editHelpText, editInfoText;
    readonly List<Image> bands = new List<Image>(), bandH0 = new List<Image>(), bandH1 = new List<Image>();
    int dragKind;          // 0:なし 1:ページの始まり 2:ページの終わり 3:段の始まり 4:段の終わり
    int dragStem;
    bool editsChanged;

    // 手回し
    bool cranking, hadAngle, crankLocked;
    double crankQ;
    float crankRate, lastAngle, idleTime, rumbleUntil;
    long lastBeatIdx;
    // テンポの測定：スティックが一番上・一番下を通った時刻
    readonly List<float> passTimes = new List<float>();
    int passDir;
    double cwFromTop;      // 一番上からの時計回りの角度（度、何周しても続けて数える）
    long lastHalf;
    double stepRatio = 1.0;   // 今の速さの段階（元のテンポの何倍か）
    float playRatio = 1f;     // 実際の再生の速さ（元のテンポの何倍か）
    float attractTimer;       // 1倍の近くで回し続けている時間
    Text crankText;

    // 画面のUI
    RectTransform stickTopRT, needleRT;
    Material paintMat;
    readonly List<RawImage> paintImgs = new List<RawImage>();
    Image rGlow, lGlow;
    Vector2 stickPos;
    float needleAngle, needleVel, paintAmount, rGlowA, lGlowA;
    Color paintCur, rGlowCol = Color.clear, lGlowCol = Color.clear;

    // 水彩の色
    Material washMat;
    RawImage washImg;
    Texture2D washFallback;
    bool washOn, washWarned;
    float washSpread, washAlpha = 0f;
    int washPatternIdx = -1;
    Color washCur;

    // ページめくりのアニメーション
    bool turning;
    RenderTexture turnRT;
    RawImage turnImg;
    Material turnMat;

    // 巻き戻し・展示用
    bool rewinding;
    bool attractLoop = true;   // 待機中（最初のページだけを繰り返している状態）
    float rewindTime, lastInputTime;
    bool[] initialMutes;

    // 右スティックでの段の選択
    int rsDir;
    float rsRepeatAt;

    // 歌詞のピント
    Material lyricMat;
    float lyricBlur;

    const string Version = "2026-11-23 設定モード";

    int Bpb => Mathf.Max(1, beatsPerBar);
    float BeatSec => 60f / Mathf.Max(1f, bpm);
    float BarTime(int bar) => (float)QToSongSec((bar - 1) * (double)Bpb);
    string EditsPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PoemPageEdits.json"));

    // =====================================================================
    // 起動
    // =====================================================================

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
        ApplyEditsFile(false);   // 前回の設定モードの内容が取り込まれずに残っていれば使う

        srcs = new AudioSource[stems.Count, 2];
        tailSrcs = new AudioSource[stems.Count, 2];
        flip = new int[stems.Count];
        tailFlip = new int[stems.Count];
        for (int s = 0; s < stems.Count; s++)
            for (int k = 0; k < 2; k++)
            {
                var a = gameObject.AddComponent<AudioSource>();
                a.playOnAwake = false;
                srcs[s, k] = a;
                var t = gameObject.AddComponent<AudioSource>();
                t.playOnAwake = false;
                tailSrcs[s, k] = t;
            }

        initialMutes = new bool[stems.Count];
        for (int s = 0; s < stems.Count; s++) initialMutes[s] = stems[s].mute;
        lastInputTime = Time.unscaledTime;

        LogMidiTracks();
        BuildAllTextures();
        BuildUI();

        currentPage = 0;
        StartPageNow(0);
        ShowPage(0);
        ready = true;

        if (debugStartPage > 0)
        {
            Debug.Log("デバッグ：ページ " + debugStartPage + " から再生します");
            JumpToPage(debugStartPage - 1);
        }
    }

    // =====================================================================
    // デバッグ：指定のページへ飛んで、そこから再生する（F1 でページ一覧）
    // =====================================================================

    bool debugHold;          // デバッグで飛んでいる間は、放置しても最初に巻き戻さない
    Vector2 debugScroll;

    void JumpToPage(int p)
    {
        p = Mathf.Clamp(p, 0, pages.Count - 1);
        StartPageNow(p);
        attractLoop = debugLoopPage;
        debugHold = true;
        lastInputTime = Time.unscaledTime;
        ShowPage(p);
    }

    void OnGUI()
    {
        if (!ready || !showDebugPanel) return;
        float k = Screen.height / 1080f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1f));

        GUILayout.BeginArea(new Rect(20f, 100f, 420f, 900f), GUI.skin.box);
        GUILayout.Label("デバッグ：ページから再生（F1で閉じる）");
        debugLoopPage = GUILayout.Toggle(debugLoopPage, " 飛んだページを繰り返す");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("待機（最初のページ）に戻す")) { debugHold = false; FinishRewind(); }
        if (GUILayout.Button("ミュートを始めの状態に"))
            for (int s = 0; s < stems.Count && initialMutes != null; s++) stems[s].mute = initialMutes[s];
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        debugScroll = GUILayout.BeginScrollView(debugScroll);
        for (int i = 0; i < pages.Count; i++)
        {
            var pg = pages[i];
            string label = (i == currentPage ? "▶ " : "　 ") + (i + 1) + "　" + (string.IsNullOrEmpty(pg.title) ? "" : pg.title + "　") +
                           BarBeat(PageStartQ(pg)) + "〜";
            if (GUILayout.Button(label, GUILayout.Height(30f))) JumpToPage(i);
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
        GUI.matrix = Matrix4x4.identity;
    }

    // =====================================================================
    // ページの自動作成・補正
    // =====================================================================

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

    [ContextMenu("今のページの小節数を一覧に書き出す")]
    void WriteBarsList()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "小節数の一覧を書き出す");
#endif
        var parts = new List<string>();
        foreach (var p in pages) parts.Add(p.lengthBars.ToString());
        pageBarsList = string.Join(", ", parts);
        if (pages.Count > 0) firstPageStartBar = pages[0].startBar;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // 一覧の小節数を、ページの頭から順番に割り当てる（歌詞画像やタイトルはそのまま）
    [ContextMenu("小節数の一覧をページに反映")]
    void ApplyBarsList()
    {
        var nums = new List<int>();
        foreach (var tok in pageBarsList.Split(new[] { ',', '、', ' ', '　', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(tok.Trim(), out int n) && n > 0) nums.Add(n);
            else { Debug.LogWarning("小節数の一覧に、数字でないもの「" + tok + "」があります。"); return; }
        }
        if (nums.Count == 0) { Debug.LogWarning("小節数の一覧が空です。"); return; }
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "小節数の一覧をページに反映");
#endif
        while (pages.Count < nums.Count) pages.Add(new PoemPage { title = "ページ" + (pages.Count + 1) });
        int bar = Mathf.Max(1, firstPageStartBar);
        for (int i = 0; i < nums.Count; i++)
        {
            var p = pages[i];
            p.startBar = bar;
            p.lengthBars = nums[i];
            if (resetBeatOffsetsOnApply) { p.startBeatOffset = 0f; p.endBeatOffset = 0f; }
            bar += nums[i];
        }
        string msg = nums.Count + "ページぶんの小節数を反映しました（" + Mathf.Max(1, firstPageStartBar) + "〜" + (bar - 1) + "小節）。";
        if (pages.Count > nums.Count) msg += "　一覧より後ろの " + (pages.Count - nums.Count) + " ページは変えていません。";
        if (Application.isPlaying) msg += "　※再生中に反映した内容は、止めると元に戻ります。止めてから反映してください。";
        Debug.Log(msg);
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

    // =====================================================================
    // 設定モードの内容の保存と取り込み
    // （再生中にインスペクターの値を変えても、止めると元に戻ってしまうので、いったんファイルに書いておく）
    // =====================================================================

    void SaveEditsFile()
    {
        var file = new PageTimingFile();
        foreach (var p in pages)
        {
            var t = new PageTiming { startBar = p.startBar, lengthBars = p.lengthBars, startBeatOffset = p.startBeatOffset, endBeatOffset = p.endBeatOffset };
            foreach (var w in p.stemWindows)
                t.stemWindows.Add(new StemWindow { stemNumber = w.stemNumber, startOffsetBeats = w.startOffsetBeats, endOffsetBeats = w.endOffsetBeats });
            file.pages.Add(t);
        }
        File.WriteAllText(EditsPath, JsonUtility.ToJson(file, true));
    }

    bool ApplyEditsFile(bool persist)
    {
        if (!File.Exists(EditsPath)) return false;
        var file = JsonUtility.FromJson<PageTimingFile>(File.ReadAllText(EditsPath));
        if (file == null || file.pages == null) return false;
#if UNITY_EDITOR
        if (persist) UnityEditor.Undo.RecordObject(this, "設定モードの変更を取り込む");
#endif
        int n = Mathf.Min(file.pages.Count, pages.Count);
        for (int i = 0; i < n; i++)
        {
            var t = file.pages[i];
            var p = pages[i];
            p.startBar = t.startBar;
            p.lengthBars = t.lengthBars;
            p.startBeatOffset = t.startBeatOffset;
            p.endBeatOffset = t.endBeatOffset;
            p.stemWindows = t.stemWindows ?? new List<StemWindow>();
        }
#if UNITY_EDITOR
        if (persist)
        {
            UnityEditor.EditorUtility.SetDirty(this);
            if (gameObject.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            File.Delete(EditsPath);
            Debug.Log("設定モードで変えたページの範囲を、インスペクターに取り込みました。シーンを保存（Ctrl+S）してください。");
        }
#endif
        return true;
    }

    [ContextMenu("設定モードの変更を取り込む")]
    void ImportEditsMenu()
    {
        if (!ApplyEditsFile(true)) Debug.Log("取り込む変更はありません。");
    }

#if UNITY_EDITOR
    // 再生を止めたときに、設定モードの内容を自動で取り込む
    [UnityEditor.InitializeOnLoadMethod]
    static void RegisterPlayModeHook()
    {
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    static readonly Dictionary<string, string> playModeSnapshots = new Dictionary<string, string>();

    static string SnapshotKey(PoemPagePlayer pp) => pp.gameObject.scene.path + "/" + pp.gameObject.name;

    static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
    {
        // 止める直前に、インスペクターの値を覚えておく
        if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
        {
            playModeSnapshots.Clear();
            foreach (var pp in FindObjectsByType<PoemPagePlayer>(FindObjectsSortMode.None))
                if (pp.keepPlayModeTweaks) playModeSnapshots[SnapshotKey(pp)] = JsonUtility.ToJson(pp);
            return;
        }
        if (state != UnityEditor.PlayModeStateChange.EnteredEditMode) return;
        foreach (var pp in FindObjectsByType<PoemPagePlayer>(FindObjectsSortMode.None))
        {
            if (playModeSnapshots.TryGetValue(SnapshotKey(pp), out var json)) pp.RestorePlayModeTweaks(json);
            pp.ApplyEditsFile(true);
        }
        playModeSnapshots.Clear();
    }

    // 再生中に変えた値を戻す。段のミュートとページの中身（歌詞画像など）は、再生前のものを使う
    void RestorePlayModeTweaks(string json)
    {
        var mutes = new bool[stems.Count];
        for (int s = 0; s < stems.Count; s++) mutes[s] = stems[s].mute;
        var keepPages = pages;
        UnityEditor.Undo.RecordObject(this, "再生中に変えた設定を残す");
        JsonUtility.FromJsonOverwrite(json, this);
        pages = keepPages;
        for (int s = 0; s < stems.Count && s < mutes.Length; s++) stems[s].mute = mutes[s];
        UnityEditor.EditorUtility.SetDirty(this);
        if (gameObject.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log("再生中にインスペクターで変えた設定を残しました。よければシーンを保存（Ctrl+S）してください。");
    }
#endif

    // =====================================================================
    // 画像の書き出し
    // =====================================================================

    [ContextMenu("ステム画像をPNGで書き出す")]
    void ExportPngs()
    {
        if (pages.Count == 0) GeneratePages();
        if (pages.Count == 0) return;

        // 再生していないときは音のデータが読み込まれていないことがあるので、先に読み込む
        foreach (var st in stems)
            if (st.clip != null && st.clip.loadState != AudioDataLoadState.Loaded) st.clip.LoadAudioData();

        tempoBuilt = false;
        bool oldEdit = editMode;
        editMode = false;
        int oldW = TexW, oldH = TexH;
        var oldTex = rowTex;
        var oldVis = rowVisible;
        var oldTexEdit = texEdit;
        var oldQ0 = axisQ0;
        var oldQ1 = axisQ1;
        TexW = Mathf.Max(64, exportWidth);
        TexH = Mathf.Max(16, exportHeight);
        BuildAllTextures();

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
        rowVisible = oldVis;
        texEdit = oldTexEdit;
        axisQ0 = oldQ0;
        axisQ1 = oldQ1;
        editMode = oldEdit;
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

    // =====================================================================
    // ページと段の範囲（すべて「拍」＝四分音符いくつ目か、で計算する）
    // =====================================================================

    double PageStartQ(PoemPage pg) => (pg.startBar - 1) * (double)Bpb + pg.startBeatOffset;
    double PageEndQ(PoemPage pg) => (pg.startBar - 1 + Mathf.Max(1, pg.lengthBars)) * (double)Bpb + pg.endBeatOffset;
    float PageStartSec(PoemPage pg) => (float)QToSongSec(PageStartQ(pg));
    float PageLen(int i) => (float)(QToSongSec(PageEndQ(pages[i])) - QToSongSec(PageStartQ(pages[i])));

    StemWindow FindWindow(PoemPage pg, int s)
    {
        foreach (var w in pg.stemWindows) if (w.stemNumber - 1 == s) return w;
        return null;
    }

    StemWindow GetOrAddWindow(PoemPage pg, int s)
    {
        var w = FindWindow(pg, s);
        if (w == null)
        {
            w = new StemWindow { stemNumber = s + 1 };
            pg.stemWindows.Add(w);
        }
        return w;
    }

    void GetWindowQ(PoemPage pg, int s, out double q0, out double q1)
    {
        q0 = PageStartQ(pg);
        q1 = PageEndQ(pg);
        var w = FindWindow(pg, s);
        if (w != null)
        {
            q0 += w.startOffsetBeats;
            q1 += w.endOffsetBeats;
        }
    }

    void GetWindow(PoemPage pg, int s, out float ws, out float we)
    {
        GetWindowQ(pg, s, out double q0, out double q1);
        ws = (float)QToSongSec(q0);
        we = (float)QToSongSec(q1);
    }

    // 拍の位置を「12小節3拍」のような表記にする
    string BarBeat(double q)
    {
        int bar = (int)Math.Floor(q / Bpb + 1e-6) + 1;
        double beat = q - (bar - 1) * (double)Bpb + 1;
        string b = Math.Abs(beat - Math.Round(beat)) < 1e-4 ? Math.Round(beat).ToString("0") : beat.ToString("0.##");
        return bar + "小節" + b + "拍";
    }

    // =====================================================================
    // テンポマップ
    // =====================================================================

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

    // 四分音符いくつ目か → 曲の頭からの秒数（firstBarOffsetSec を含まない）
    double QToSec(double q)
    {
        if (!tempoBuilt) BuildTempoMap(false);
        if (tempoMap == null) return q * 60.0 / Mathf.Max(1f, bpm);
        var seg = tempoMap[0];
        for (int i = 1; i < tempoMap.Count && tempoMap[i].q <= q; i++) seg = tempoMap[i];
        return seg.sec + (q - seg.q) * seg.secPerQ;
    }

    // 秒数 → 四分音符いくつ目か（QToSec の逆）
    double SecToQ(double sec)
    {
        if (!tempoBuilt) BuildTempoMap(false);
        if (tempoMap == null) return sec / (60.0 / Mathf.Max(1f, bpm));
        var seg = tempoMap[0];
        for (int i = 1; i < tempoMap.Count && tempoMap[i].sec <= sec; i++) seg = tempoMap[i];
        return seg.q + (sec - seg.sec) / seg.secPerQ;
    }

    double QToSongSec(double q) => firstBarOffsetSec + QToSec(q);
    double SongSecToQ(double t) => SecToQ(t - firstBarOffsetSec);

    // 途中から書き出した音源の頭が、曲の何秒目にあたるか
    float ClipOffset(int s) => stems[s].clipStartBar > 1 ? BarTime(stems[s].clipStartBar) : 0f;

    // =====================================================================
    // 再生のしくみ
    // =====================================================================

    // アウフタクトのために、ページの始まりよりどれだけ前から鳴らし始める必要があるか
    float MaxPreRoll()
    {
        float m = 0f;
        foreach (var p in pages)
        {
            float ps = PageStartSec(p);
            for (int s = 0; s < stems.Count; s++)
            {
                GetWindow(p, s, out float ws, out _);
                m = Mathf.Max(m, ps - ws);
            }
        }
        return m;
    }

    // 指定のページを、今すぐ頭から鳴らし直す
    void StartPageNow(int p)
    {
        StopAllSources();
        float preRoll = MaxPreRoll();
        decisionLead = scheduleAhead + preRoll;
        double first = AudioSettings.dspTime + 0.2 + preRoll;
        currentPage = p;
        pendingPage = -1;
        nextDecided = false;
        ScheduleIteration(p, first);
        nextIterStart = first + PageLen(p);
    }

    // 指定のページを、曲の songSec 秒目のところから鳴らし始める（手回しのあと、途中から自動再生に戻るとき）
    void StartPageFrom(int p, float songSec)
    {
        StopAllSources();
        decisionLead = scheduleAhead + MaxPreRoll();
        double baseT = AudioSettings.dspTime + 0.08;
        double boundary = baseT - (songSec - PageStartSec(pages[p]));
        currentPage = p;
        pendingPage = -1;
        nextDecided = false;
        ScheduleIteration(p, boundary, songSec);
        nextIterStart = boundary + PageLen(p);
    }

    void StopAllSources()
    {
        cranking = false;
        rewinding = false;
        atEnd = false;
        foreach (var a in srcs) { a.Stop(); a.pitch = 1f; a.volume = 1f; }
        foreach (var a in tailSrcs) a.Stop();
    }

    // 「boundary」の時刻にページの始まりが鳴るように、全段の音源を予約する。
    // 段ごとに2つの AudioSource を交互に使うので、アウフタクトが前のループの終わりと重なっても切れない。
    void ScheduleIteration(int pageIdx, double boundary, float fromSongSec = float.NegativeInfinity)
    {
        var page = pages[pageIdx];
        float pageStart = PageStartSec(page);

        for (int s = 0; s < stems.Count; s++)
        {
            var clip = stems[s].clip;
            if (clip == null)
            {
                if (!loggedFirstSchedule) Debug.Log("予約「" + stems[s].name + "」 音源なしのためスキップ");
                continue;
            }

            GetWindow(page, s, out float ws, out float we);
            ws = Mathf.Max(ws, fromSongSec);   // 途中から鳴らすときは、そこから
            if (we <= ws) continue;
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
            a.pitch = 1f;
            a.volume = 1f;
            a.PlayScheduled(dspStart);
            a.SetScheduledEndTime(dspStart + (we - ws));

            // 区切りの瞬間から、余韻だけを別に鳴らす（次のループの頭と重なってもよい）
            if (loopTail && tailLevel > 0f && (!tailOnlyFirstPage || pageIdx == 0))
            {
                var tail = GetTail(pageIdx, s, clip, ws, we);
                if (tail != null)
                {
                    var t = tailSrcs[s, tailFlip[s]];
                    tailFlip[s] = 1 - tailFlip[s];
                    t.Stop();
                    t.clip = tail;
                    t.mute = stems[s].mute;
                    t.PlayScheduled(dspStart + (we - ws));
                }
            }

            if (!loggedFirstSchedule)
                Debug.Log("予約「" + stems[s].name + "」 " + (ws + off).ToString("0.00") + "〜" + (we + off).ToString("0.00") + "秒　開始まで " +
                          (dspStart - AudioSettings.dspTime).ToString("0.00") + "秒　isPlaying " + a.isPlaying);
        }
        loggedFirstSchedule = true;
    }

    void Update()
    {
        if (!ready) return;
        CheckTailParams();
        HandleInput();
        HandleStick();
        CheckIdleReturn();
        if (editMode) HandleEditMouse();

        for (int s = 0; s < stems.Count; s++)
        {
            srcs[s, 0].mute = stems[s].mute;
            srcs[s, 1].mute = stems[s].mute;
            tailSrcs[s, 0].mute = stems[s].mute;
            tailSrcs[s, 1].mute = stems[s].mute;
        }

        double now = AudioSettings.dspTime;

        if (!cranking && !nextDecided && now >= nextIterStart - decisionLead)
        {
            // 待機中（最初のページの繰り返し）と設定モードでは同じページを繰り返し、それ以外は自動で次のページへ
            int autoNext = attractLoop || editMode || !autoPageTurn ? currentPage : Mathf.Min(currentPage + 1, pages.Count - 1);
            upcomingPage = pendingPage >= 0 ? pendingPage : autoNext;
            pendingPage = -1;
            ScheduleIteration(upcomingPage, nextIterStart);
            nextDecided = true;
        }

        if (!cranking && nextDecided && now >= nextIterStart)
        {
            currentPage = upcomingPage;
            nextIterStart += PageLen(currentPage);
            nextDecided = false;
        }

        if (currentPage != shownPage && !turning)
        {
            if (pageTurnAnim && turnMat != null && shownPage >= 0) StartCoroutine(PageTurnRoutine(currentPage));
            else ShowPage(currentPage);
        }
        UpdateVisuals(now);
        if (editMode) UpdateEditOverlay();
    }

    // =====================================================================
    // ページめくり：前のページの画面を写し取り、その紙が角からめくれて、下から新しいページが現れる
    // =====================================================================

    IEnumerator PageTurnRoutine(int p)
    {
        turning = true;
        int dir = p > shownPage ? 1 : -1;

        // 前のページが表示されている今のフレームを写し取る
        yield return new WaitForEndOfFrame();
        if (turnRT == null || turnRT.width != Screen.width || turnRT.height != Screen.height)
        {
            if (turnRT != null) turnRT.Release();
            turnRT = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            turnRT.Create();
        }
        ScreenCapture.CaptureScreenshotIntoRenderTexture(turnRT);

        // 写し取った前のページを上に重ねてから、下に新しいページを用意する
        turnMat.SetFloat("_Progress", 0f);
        turnMat.SetFloat("_Dir", dir);
        turnMat.SetFloat("_Aspect", (float)Screen.width / Screen.height);
        turnMat.SetFloat("_FlipY", SystemInfo.graphicsUVStartsAtTop ? 1f : 0f);
        turnMat.SetColor("_PaperColor", paperColor);
        turnMat.SetFloat("_Shadow", pageTurnShadow);
        turnMat.SetFloat("_ShowThrough", pageShowThrough);
        turnImg.texture = turnRT;
        turnImg.enabled = true;
        ShowPage(p);

        float dur = Mathf.Max(0.05f, pageTurnSec / (rewinding ? rewindTurnSpeedUp : 1f));
        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / dur);
            k = k * k * (3f - 2f * k);   // ゆっくり持ち上がって、途中で速く、最後はふわっと落ちる
            turnMat.SetFloat("_Progress", k);
            yield return null;
        }
        turnImg.enabled = false;
        turning = false;
    }

    // =====================================================================
    // 画面のUI画像（スティック・メーター・絵の具の模様・LRボタンの光）
    // 画像はすべて 1920×1080 のアートボードのまま書き出したものを、画面いっぱいに重ねる
    // =====================================================================

    RawImage FullScreen(string name, Transform parent, Texture2D tex, Vector2 pivotPx)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.pivot = new Vector2(pivotPx.x / 1920f, 1f - pivotPx.y / 1080f);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<RawImage>();
        img.texture = tex;
        img.raycastTarget = false;
        return img;
    }

    // 段を名前で探す。大文字・小文字や前後の空白は無視し、日本語と英語の名前どちらでも見つかるようにする
    static readonly string[][] stemAliases =
    {
        new[] { "ハーモニー", "harmony", "harmonies" },
        new[] { "メロディ", "メロディー", "melody", "piano" },
        new[] { "ボーカル", "vocal", "vocals", "vo" },
    };

    int FindStem(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        string key = name.Trim().ToLowerInvariant();
        int idx = stems.FindIndex(st => st.name != null && st.name.Trim().ToLowerInvariant() == key);
        if (idx >= 0) return idx;
        foreach (var group in stemAliases)
        {
            if (Array.IndexOf(group, key) < 0) continue;
            foreach (var alias in group)
            {
                idx = stems.FindIndex(st => st.name != null && st.name.Trim().ToLowerInvariant() == alias);
                if (idx >= 0) return idx;
            }
        }
        return -1;
    }

    // 1920×1080（16:9 で大きいもの）はアートボードのまま書き出した画像とみなして、画面いっぱいに重ねる
    static bool IsFullArtboard(Texture2D t) => t.width >= 1280 && Mathf.Abs((float)t.width / t.height - 16f / 9f) < 0.02f;

    // 切り抜いて書き出した画像を、元の大きさのまま指定の位置に置く（pivot が位置の基準点、回転の中心にもなる）
    RawImage Placed(string name, Transform parent, Texture2D tex, Vector2 posPx, Vector2 pivot, float scale)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = pivot;
        rt.sizeDelta = new Vector2(tex.width, tex.height) * Mathf.Max(0.01f, scale);
        rt.anchoredPosition = new Vector2(posPx.x, -posPx.y);
        var img = go.AddComponent<RawImage>();
        img.texture = tex;
        img.raycastTarget = false;
        return img;
    }

    Vector2 stickTopBase;
    bool stickPlaced, needlePlaced;

    void BuildUIParts(Transform root)
    {
        if (uiBackground != null) FullScreen("UIBackground", root, uiBackground, new Vector2(960f, 540f));

        var paintShader = Shader.Find("Hidden/PaintBlot");
        if (paintImages != null && paintImages.Count > 0)
        {
            if (paintShader != null) paintMat = new Material(paintShader);
            else Debug.LogWarning("PaintBlot.shader が見つからないので、絵の具の模様に色は付きません。Assets に入れてください。");
            for (int i = 0; i < paintImages.Count; i++)
            {
                var tex = paintImages[i];
                if (tex == null) continue;
                var center = i < paintCenters.Count ? paintCenters[i] : new Vector2(960f, 540f);
                var img = IsFullArtboard(tex) ? FullScreen("Paint", root, tex, new Vector2(960f, 540f))
                                              : Placed("Paint", root, tex, center, new Vector2(0.5f, 0.5f), paintScale);
                if (paintMat != null) img.material = paintMat;
                paintImgs.Add(img);
            }
        }
        paintCur = paintColor;

        if (stickTopImage != null)
        {
            stickTopRT = IsFullArtboard(stickTopImage)
                ? FullScreen("StickTop", root, stickTopImage, new Vector2(960f, 540f)).rectTransform
                : Placed("StickTop", root, stickTopImage, stickTopCenter, new Vector2(0.5f, 0.5f), stickTopScale).rectTransform;
            stickTopBase = stickTopRT.anchoredPosition;
            stickPlaced = !IsFullArtboard(stickTopImage);
        }
        if (needleImage != null)
        {
            // 針は根もとを回転の中心にする（根もとは動かず、先だけが振れる）
            needleRT = IsFullArtboard(needleImage)
                ? FullScreen("Needle", root, needleImage, needlePivot).rectTransform
                : Placed("Needle", root, needleImage, needlePivot, needleRootInImage, needleScale).rectTransform;
            needlePlaced = !IsFullArtboard(needleImage);
        }
        needleAngle = 0f;

        var glowTex = MakeGlow(128);
        rGlow = MakeGlowImage(root, "RGlow", glowTex, rButtonCenter);
        lGlow = MakeGlowImage(root, "LGlow", glowTex, lButtonCenter);
    }

    Image MakeGlowImage(Transform root, string name, Texture2D tex, Vector2 center)
    {
        var img = MakeImage(root, name, center.x - buttonGlowSize * 0.5f, center.y - buttonGlowSize * 0.5f, buttonGlowSize, buttonGlowSize, Color.clear);
        img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        return img;
    }

    // ふんわりした光（中心が明るく、外へ向かって消える）
    static Texture2D MakeGlow(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = (new Vector2(x, y) / (size - 1) * 2f - Vector2.one).magnitude;
                float a = Mathf.Clamp01(1f - r);
                a = a * a * (0.6f + 0.4f * a);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return tex;
    }

    // 今の再生の速さ（元のテンポの何倍か。向きは無視）
    float CurrentSpeedRatio()
    {
        if (!cranking) return 1f;
        if (atEnd) return 0f;
        if (rewinding || crankMode == CrankMode.位置) return Mathf.Abs((float)(crankRate * SecPerQAt(crankQ)));
        return Mathf.Abs(playRatio);
    }

    void UpdateUIParts(double now)
    {
        float dt = Time.unscaledDeltaTime;

        // 再生中にインスペクターで位置や大きさを変えても、すぐ反映する（位置合わせ用）
        if (stickTopRT != null && stickPlaced)
        {
            stickTopBase = new Vector2(stickTopCenter.x, -stickTopCenter.y);
            stickTopRT.sizeDelta = new Vector2(stickTopImage.width, stickTopImage.height) * Mathf.Max(0.01f, stickTopScale);
        }
        if (needleRT != null && needlePlaced)
        {
            needleRT.pivot = needleRootInImage;
            needleRT.sizeDelta = new Vector2(needleImage.width, needleImage.height) * Mathf.Max(0.01f, needleScale);
            needleRT.anchoredPosition = new Vector2(needlePivot.x, -needlePivot.y);
        }

        // ① スティックの絵：入力に合わせて上の部分が動く
        if (stickTopRT != null)
        {
            var gp = Gamepad.current;
            Vector2 v = gp != null ? gp.leftStick.ReadValue() : Vector2.zero;
            stickPos = Vector2.Lerp(stickPos, v * stickTravel, 1f - Mathf.Exp(-stickFollow * dt));
            stickTopRT.anchoredPosition = stickTopBase + stickPos;
        }

        // ③ メーター：速さ0で左端、ぴったりで12時、速すぎると右へ。ばねのように少し揺れながら動く
        if (needleRT != null)
        {
            float r = CurrentSpeedRatio();
            float target = r <= 1f ? (r - 1f) * needleMaxAngle
                                   : Mathf.Min(1f, (r - 1f) / (meterMaxRatio - 1f)) * needleMaxAngle;
            float k = needleStiffness, c = 2f * Mathf.Sqrt(k) * needleDamping;
            int steps = Mathf.Max(1, Mathf.CeilToInt(dt / 0.005f));
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                needleVel += (k * (target - needleAngle) - c * needleVel) * h;
                needleAngle += needleVel * h;
            }
            needleAngle = Mathf.Clamp(needleAngle, -needleMaxAngle - 8f, needleMaxAngle + 8f);
            needleRT.localEulerAngles = new Vector3(0f, 0f, -needleAngle);
        }

        // ④ 絵の具の模様：ハーモニーが鳴っている間だけ色がにじむ
        if (paintMat != null)
        {
            int hs = FindStem(washStemName);
            bool on = hs >= 0 && !stems[hs].mute && rowVisible[shownPage, hs];
            paintAmount = Mathf.MoveTowards(paintAmount, on ? 1f : 0f, dt / Mathf.Max(0.05f, paintFadeSec));
            var pc = pages[shownPage].washColor;
            var target = pc.a > 0f ? pc : paintColor;
            paintCur = Color.Lerp(paintCur, target, 1f - Mathf.Exp(-dt * 1.5f));
            float eased = paintAmount * paintAmount * (3f - 2f * paintAmount);
            paintMat.SetFloat("_Amount", eased);
            paintMat.SetColor("_Color", paintCur);
            paintMat.SetFloat("_Gain", paintColorGain);
            paintMat.SetFloat("_Wobble", paintWobble);
        }

        // ⑤ LRボタンの光：ページの最後の小節で、拍に合わせてRが光る。逆再生でページの最初の小節に来たらLが光る
        if (rGlow != null)
        {
            var pg = pages[currentPage];
            double ps = PageStartQ(pg), pe = PageEndQ(pg);
            double q;
            if (cranking) q = crankQ;
            else q = SongSecToQ(PageStartSec(pg) + (now - (nextIterStart - PageLen(currentPage))));
            float ratioSigned = cranking ? (crankMode == CrankMode.テンポ ? playRatio : (float)(crankRate * SecPerQAt(crankQ))) : 1f;
            bool usable = !rewinding && !attractLoop && !editMode && !atEnd && !autoPageTurn;
            bool fwd = ratioSigned > 0.02f, rev = ratioSigned < -0.02f;
            float frac = (float)(q - Math.Floor(q));

            float rT = 0f, lT = 0f;
            if (usable && fwd && currentPage < pages.Count - 1 && q >= pe - Bpb)
                rT = 0.25f + 0.75f * Mathf.Exp(-frac * 5f);
            if (usable && rev && currentPage > 0 && q < ps + Bpb)
                lT = 0.25f + 0.75f * Mathf.Exp(-(1f - frac) * 5f);
            // 押して予約されたら、めくれるまで緑に灯ったまま
            bool rPressed = !rewinding && pendingPage > currentPage;
            bool lPressed = !rewinding && pendingPage >= 0 && pendingPage < currentPage;
            if (rPressed) rT = 0.9f;
            if (lPressed) lT = 0.9f;

            rGlowA = Mathf.Lerp(rGlowA, rT, 1f - Mathf.Exp(-25f * dt));
            lGlowA = Mathf.Lerp(lGlowA, lT, 1f - Mathf.Exp(-25f * dt));
            rGlowCol = Color.Lerp(rGlowCol, rPressed ? buttonPressedColor : buttonGlowColor, 1f - Mathf.Exp(-20f * dt));
            lGlowCol = Color.Lerp(lGlowCol, lPressed ? buttonPressedColor : buttonGlowColor, 1f - Mathf.Exp(-20f * dt));
            rGlow.color = new Color(rGlowCol.r, rGlowCol.g, rGlowCol.b, rGlowCol.a * rGlowA);
            lGlow.color = new Color(lGlowCol.r, lGlowCol.g, lGlowCol.b, lGlowCol.a * lGlowA);
        }
    }

    // =====================================================================
    // 水彩の色：指定の段が鳴っている間だけ、画面に色がにじんで広がる
    // =====================================================================

    void UpdateWash()
    {
        if (washMat == null) return;
        float dt = Time.unscaledDeltaTime;

        int ws = FindStem(washStemName);
        if (ws < 0 && washEnabled && !washWarned)
        {
            washWarned = true;
            Debug.LogWarning("水彩の色：段「" + washStemName + "」が見つかりません。Wash Stem Name を段の名前に合わせてください。");
        }
        bool want = washEnabled && ws >= 0 && !stems[ws].mute && rowVisible[shownPage, ws] && !rewinding;

        if (want && !washOn)
        {
            washOn = true;
            // 乾いていたら、新しい模様と落とす点を選んで、ゼロからにじませる
            if (washAlpha < 0.05f)
            {
                washSpread = 0f;
                NextWashPattern();
            }
        }
        if (!want) washOn = false;

        if (washOn)
        {
            washSpread = Mathf.MoveTowards(washSpread, 1f, dt / Mathf.Max(0.05f, washInSec));
            washAlpha = Mathf.MoveTowards(washAlpha, 1f, dt / 0.25f);
        }
        else washAlpha = Mathf.MoveTowards(washAlpha, 0f, dt / Mathf.Max(0.05f, washOutSec));

        var pc = pages[shownPage].washColor;
        var target = pc.a > 0f ? pc : washColor;
        washCur = Color.Lerp(washCur, target, 1f - Mathf.Exp(-dt * 3f / Mathf.Max(0.1f, washColorChangeSec)));

        float spreadEased = 1f - (1f - washSpread) * (1f - washSpread);   // はじめ速く、だんだんゆっくり広がる
        float alphaEased = washAlpha * washAlpha * (3f - 2f * washAlpha);
        washMat.SetFloat("_Spread", spreadEased);
        washMat.SetFloat("_Alpha", alphaEased);
        washMat.SetFloat("_Strength", washStrength);
        washMat.SetFloat("_Edge", washEdge);
        washMat.SetColor("_Color", washCur);
        washMat.SetFloat("_Aspect", (float)Screen.width / Mathf.Max(1, Screen.height));
        washImg.enabled = alphaEased > 0.001f;
    }

    void NextWashPattern()
    {
        Texture2D tex;
        if (washPatterns != null && washPatterns.Count > 0)
        {
            washPatternIdx = (washPatternIdx + 1) % washPatterns.Count;
            tex = washPatterns[washPatternIdx];
        }
        else
        {
            if (washFallback == null) washFallback = MakeWashFallback(512, 288);
            tex = washFallback;
        }
        washImg.texture = tex;
        washMat.SetVector("_Origin", new Vector4(UnityEngine.Random.Range(0.2f, 0.8f), UnityEngine.Random.Range(0.25f, 0.75f), 0f, 0f));
    }

    // パターン画像がないとき用の、ふんわりした模様
    static Texture2D MakeWashFallback(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float sx = UnityEngine.Random.value * 100f, sy = UnityEngine.Random.value * 100f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float n = 0.55f * Mathf.PerlinNoise(x * 0.006f + sx, y * 0.006f + sy)
                        + 0.30f * Mathf.PerlinNoise(x * 0.02f + sy, y * 0.02f + sx)
                        + 0.15f * Mathf.PerlinNoise(x * 0.08f + 7f, y * 0.08f + 3f);
                float dens = Mathf.Clamp01((n - 0.25f) * 1.6f);
                float v = 1f - dens;
                tex.SetPixel(x, y, new Color(v, v, v, 1f));
            }
        tex.Apply();
        return tex;
    }

    // =====================================================================
    // 左スティックで手回し
    // =====================================================================

    void HandleStick()
    {
        var gp = Gamepad.current;
        Vector2 v = gp != null && stickScrub ? gp.leftStick.ReadValue() : Vector2.zero;
        float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);

        // スティックの角度の変化 → 進む拍数（時計回りで進む）
        float forward = 0f;
        float tNow = Time.unscaledTime;
        if (v.magnitude > 0.6f)
        {
            float ang = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            if (hadAngle)
            {
                float d = -Mathf.DeltaAngle(lastAngle, ang);   // 時計回りがプラス
                forward = d / 360f * beatsPerTurn;
                cwFromTop += d;
            }
            else
            {
                cwFromTop = 90.0 - ang;                       // 一番上を0度として測りはじめる
                lastHalf = (long)Math.Floor(cwFromTop / 180.0);
            }
            lastAngle = ang;
            hadAngle = true;

            // 一番上（0度）か一番下（180度）を通ったら、その時刻を記録する
            long half = (long)Math.Floor(cwFromTop / 180.0);
            if (half != lastHalf)
            {
                int dir = half > lastHalf ? 1 : -1;
                lastHalf = half;
                if (dir != passDir || (passTimes.Count > 0 && tNow - passTimes[passTimes.Count - 1] > 2f)) passTimes.Clear();
                passDir = dir;
                passTimes.Add(tNow);
                while (passTimes.Count > tempoAverageCount + 1) passTimes.RemoveAt(0);
            }
        }
        else hadAngle = false;

        if (!cranking)
        {
            if (Mathf.Abs(forward) < 0.02f) return;
            EnterCrank();
        }

        if (Mathf.Abs(forward) > 0.0001f)
        {
            idleTime = 0f;
            lastInputTime = tNow;
            if (rewinding) { rewinding = false; passTimes.Clear(); }   // 巻き戻し中にまた回しはじめたら、そこから手回し
        }
        else idleTime += dt;

        if (!rewinding && idleTime > resumeAfterSec)
        {
            if (afterRelease == AfterRelease.その場から自動再生) { ExitCrank(); return; }
            StartRewind();
        }

        if (rewinding)
        {
            // 少しずつ速くなりながら、曲の最初へ巻き戻る
            rewindTime += dt;
            crankLocked = false;
            double naturalR = 1.0 / Math.Max(1e-4, SecPerQAt(crankQ));
            float speedX = Mathf.Lerp(rewindStartSpeed, rewindMaxSpeed, Mathf.SmoothStep(0f, 1f, rewindTime / rewindAccelSec));
            crankRate = Mathf.Lerp(crankRate, -(float)(speedX * naturalR), 1f - Mathf.Exp(-3f * dt));
            playRatio = (float)(crankRate / naturalR);
        }
        else if (crankMode == CrankMode.位置)
        {
            crankLocked = false;
            float target = forward / dt;
            crankRate = Mathf.Lerp(crankRate, target, 1f - Mathf.Exp(-scrubResponse * dt));
            playRatio = (float)(crankRate * SecPerQAt(crankQ));
        }
        else
        {
            // 一番上・一番下を通った間隔（＝半周）の平均から、回すテンポ（拍/秒）を求める
            double natural = 1.0 / Math.Max(1e-4, SecPerQAt(crankQ));   // 元のテンポ（拍/秒）
            double ratio = crankRate / natural;                          // まだ測れていない間は今の速さのまま
            if (passTimes.Count >= 2)
            {
                float avgInterval = (passTimes[passTimes.Count - 1] - passTimes[0]) / (passTimes.Count - 1);
                // 次の点がなかなか来ないときは、待っている時間に合わせて遅くしていく
                float since = tNow - passTimes[passTimes.Count - 1];
                float interval = Mathf.Max(avgInterval, since);
                double measured = passDir * (beatsPerTurn * 0.5) / Math.Max(0.05, interval);
                ratio = measured / natural;
            }
            // 1倍のまわりは「磁石」：近ければすぐ、そこそこ近くでもしばらく回し続ければ吸い付き、一度吸い付いたら離れにくい
            double dev = Math.Abs(Math.Abs(ratio) - 1.0);
            bool moving = Math.Abs(ratio) > 0.01;
            if (moving && dev < lockAttractRange) attractTimer += dt; else attractTimer = 0f;
            if (crankLocked) crankLocked = moving && dev < Math.Max(lockHoldRange, lockRange);
            else crankLocked = moving && (dev < lockRange || attractTimer >= lockAttractSec);
            if (crankLocked) ratio = Math.Sign(ratio);

            // 速さは speedStep 刻みの段階にする。今の段階から刻みの0.6倍以上ずれたときだけ、次の段階へ移る（行ったり来たりしない）
            double step = Math.Max(0.01, speedStep);
            if (crankLocked) stepRatio = ratio;
            else if (Math.Abs(ratio - stepRatio) > step * 0.6) stepRatio = Math.Round(ratio / step) * step;
            // 再生の速さ（何倍か）そのものをなめらかに目標の段階へ寄せ、着いたらぴったりその値に止める（1倍なら完全に1倍）
            playRatio = Mathf.Lerp(playRatio, (float)stepRatio, 1f - Mathf.Exp(-tempoFollow * dt));
            if (Mathf.Abs(playRatio - (float)stepRatio) < 0.003f) playRatio = (float)stepRatio;
            crankRate = (float)(playRatio * natural);
        }

        // ページの範囲の中でぐるぐる回る。ページ送りが予約されていれば、区切りを越えたところで次のページへ
        var pg = pages[currentPage];
        double ps = PageStartQ(pg), pe = PageEndQ(pg);
        // 鳴っている音があれば、その音の位置を「今の位置」にする（フレームの計算と音がずれて、位置を直すたびに音が揺れるのを防ぐ）
        int m = masterStem;
        if (m >= 0 && srcs[m, 0].isPlaying && Mathf.Abs(playRatio) > 0.02f && Mathf.Abs(playRatio) <= 3f)
            crankQ = SongSecToQ(srcs[m, 0].time + ClipOffset(m));
        else
            crankQ += crankRate * dt;
        if (rewinding && crankQ < ps)
        {
            // 巻き戻しはページをさかのぼり、最初のページの頭に着いたら終わり
            if (currentPage == 0) { FinishRewind(); return; }
            double under = ps - crankQ;
            currentPage--;
            crankQ = PageEndQ(pages[currentPage]) - under;
        }
        // 区切りを越えたら自動でページをめくる（設定モードでは同じページの中を回る）
        bool autoTurn = autoPageTurn && !editMode;
        // 予約されたページは、その向きに区切りを越えたときだけ使う（次のページの予約で、逆回しのときに飛ばない）
        int nextP = pendingPage > currentPage ? pendingPage : (autoTurn && currentPage < pages.Count - 1 ? currentPage + 1 : -1);
        int prevP = pendingPage >= 0 && pendingPage < currentPage ? pendingPage : (autoTurn && currentPage > 0 ? currentPage - 1 : -1);
        if (crankQ >= pe && nextP >= 0 && nextP != currentPage)
        {
            double over = crankQ - pe;
            currentPage = nextP;
            pendingPage = -1;
            crankQ = PageStartQ(pages[currentPage]) + over;
        }
        else if (crankQ < ps && prevP >= 0 && prevP != currentPage)
        {
            double under = ps - crankQ;
            currentPage = prevP;
            pendingPage = -1;
            crankQ = PageEndQ(pages[currentPage]) - under;
        }
        pg = pages[currentPage];
        ps = PageStartQ(pg);
        pe = PageEndQ(pg);
        double len = pe - ps;

        // 最後のページは、終わりまで来たらそこで止まる（ループしない）。手を離すと最初へ巻き戻る
        bool lastPage = !editMode && currentPage == pages.Count - 1 && pendingPage < 0;
        if (lastPage && crankQ >= pe)
        {
            crankQ = pe - 1e-6;
            if (crankRate > 0f) crankRate = 0f;
            if (!atEnd) { atEnd = true; PlayEndTails(); }
        }
        else if (crankQ < pe - 0.01) atEnd = false;

        if (len > 0 && !atEnd)
        {
            while (crankQ >= pe) crankQ -= len;
            while (crankQ < ps) crankQ += len;
        }
        if (atEnd) SilenceCrankSources();
        else DriveCrankAudio();
        BeatRumble(gp);
    }

    bool atEnd;

    void SilenceCrankSources()
    {
        for (int s = 0; s < stems.Count; s++)
        {
            srcs[s, 0].volume = 0f;
            srcs[s, 0].pitch = 0f;
        }
    }

    // 曲の終わりに着いたら、最後の音の余韻だけを残す
    void PlayEndTails()
    {
        if (tailLevel <= 0f) return;
        var pg = pages[currentPage];
        for (int s = 0; s < stems.Count; s++)
        {
            var clip = stems[s].clip;
            if (clip == null) continue;
            GetWindow(pg, s, out float ws, out float we);
            float off = ClipOffset(s);
            ws = Mathf.Max(0f, ws - off);
            we = Mathf.Min(we - off, clip.length);
            if (we <= ws) continue;
            var tail = GetTail(currentPage, s, clip, ws, we);
            if (tail == null) continue;
            var t = tailSrcs[s, tailFlip[s]];
            tailFlip[s] = 1 - tailFlip[s];
            t.Stop();
            t.clip = tail;
            t.mute = stems[s].mute;
            t.Play();
        }
    }

    void StartRewind()
    {
        atEnd = false;
        rewinding = true;
        rewindTime = 0f;
        pendingPage = -1;
        passTimes.Clear();
        Gamepad.current?.ResetHaptics();
        rumbleUntil = 0f;
    }

    // 最初のページに戻って、展示の始めの状態（最初のページの繰り返し）にする
    void FinishRewind()
    {
        rewinding = false;
        passTimes.Clear();
        Gamepad.current?.ResetHaptics();
        rumbleUntil = 0f;
        if (resetMutesOnReturn && initialMutes != null)
        {
            var on = new List<string>();
            for (int s = 0; s < stems.Count; s++)
            {
                stems[s].mute = initialMutes[s];
                if (!initialMutes[s]) on.Add(stems[s].name);
            }
            Debug.Log("最初に戻りました。鳴っている段：" + (on.Count > 0 ? string.Join("、", on) : "なし"));
        }
        lastInputTime = Time.unscaledTime;
        StartPageNow(0);
        attractLoop = true;
        ShowPage(0);
    }

    // しばらく誰も触らなければ、今の位置から最初のページへ巻き戻す（展示用）
    void CheckIdleReturn()
    {
        // しばらく誰も触らなければ、ミュートだけ始めの状態に戻す（待機中にミュートを解除されたままにならないように）
        if (!cranking && !editMode && idleMuteResetSec > 0f && initialMutes != null &&
            Time.unscaledTime - lastInputTime >= idleMuteResetSec)
        {
            bool changed = false;
            for (int s = 0; s < stems.Count; s++)
                if (stems[s].mute != initialMutes[s]) { stems[s].mute = initialMutes[s]; changed = true; }
            if (changed) Debug.Log("操作がないので、ミュートを始めの状態に戻しました。");
        }

        if (cranking || editMode || debugHold || idleReturnSec <= 0f) return;
        if (Time.unscaledTime - lastInputTime < idleReturnSec) return;
        bool mutesChanged = false;
        if (resetMutesOnReturn && initialMutes != null)
            for (int s = 0; s < stems.Count; s++) if (stems[s].mute != initialMutes[s]) mutesChanged = true;
        if (currentPage == 0 && !mutesChanged) { lastInputTime = Time.unscaledTime; return; }
        if (currentPage == 0) { FinishRewind(); return; }
        EnterCrank();
        StartRewind();
    }

    double SecPerQAt(double q) => (QToSongSec(q + 0.01) - QToSongSec(q)) / 0.01;

    // 拍を越えるたびに、コントローラーを短く振動させる（小節の頭は強め）
    void BeatRumble(Gamepad gp)
    {
        if (gp == null) return;
        long bi = (long)Math.Floor(crankQ);
        if (bi != lastBeatIdx)
        {
            lastBeatIdx = bi;
            if (beatRumble && Mathf.Abs(crankRate) > 0.05f)
            {
                bool bar = ((bi % Bpb) + Bpb) % Bpb == 0;
                gp.SetMotorSpeeds(bar ? 0.4f : 0.12f, bar ? 0.6f : 0.2f);
                rumbleUntil = Time.unscaledTime + (bar ? 0.07f : 0.04f);
            }
        }
        if (rumbleUntil > 0f && Time.unscaledTime > rumbleUntil)
        {
            gp.SetMotorSpeeds(0f, 0f);
            rumbleUntil = 0f;
        }
    }

    void OnDisable()
    {
        Gamepad.current?.ResetHaptics();
    }

    void EnterCrank()
    {
        attractLoop = false;
        debugHold = false;
        // 今鳴っている位置から手回しを始める
        var page = pages[currentPage];
        double iterStart = nextIterStart - PageLen(currentPage);
        double songT = PageStartSec(page) + (AudioSettings.dspTime - iterStart);
        double q = SongSecToQ(songT);
        double ps = PageStartQ(page), pe = PageEndQ(page);
        if (q < ps || q >= pe) q = ps;

        // 自動再生のほうで次のページへの切り替えがすでに決まっていたら、手回しの予約として引き継ぐ
        if (nextDecided && upcomingPage != currentPage) pendingPage = upcomingPage;
        nextDecided = false;

        StopAllSources();
        cranking = true;
        crankQ = q;
        lastBeatIdx = (long)Math.Floor(q);
        idleTime = 0f;
        // テンポモードでは、いま鳴っていた速さ（1倍）から始めて、回すテンポに寄せていく
        crankRate = crankMode == CrankMode.テンポ ? (float)(1.0 / Math.Max(1e-4, SecPerQAt(q))) : 0f;
        stepRatio = 1.0;
        playRatio = crankMode == CrankMode.テンポ ? 1f : 0f;
        masterStem = -1;
        for (int s = 0; s < stems.Count; s++)
        {
            var clip = stems[s].clip;
            if (clip == null) continue;
            var a = srcs[s, 0];
            a.clip = clip;
            a.pitch = 0f;
            a.volume = 0f;
            a.Play();
        }
        if (currentPage != shownPage) ShowPage(currentPage);
    }

    // 回す速さに合わせて、各段の再生速度（逆回しも）と位置を合わせる
    void DriveCrankAudio()
    {
        var pg = pages[currentPage];
        double t = QToSongSec(crankQ);
        // AudioSource の速さは±3倍が上限。それより速いときは位置が飛び飛びになり、テープの早戻しのように「ばばば」と鳴る
        float pitch = Mathf.Clamp(playRatio, -3f, 3f);
        if (Mathf.Abs(pitch) < 0.02f) pitch = 0f;
        int newMaster = -1;

        for (int s = 0; s < stems.Count; s++)
        {
            var clip = stems[s].clip;
            if (clip == null) continue;
            var a = srcs[s, 0];
            GetWindowQ(pg, s, out double q0, out double q1);
            float local = (float)t - ClipOffset(s);
            bool valid = crankQ >= q0 && crankQ < q1 && local >= 0f && local < clip.length - 0.02f;
            a.volume = valid ? (rewinding ? rewindVolume : 1f) : 0f;
            if (!valid) { a.pitch = 0f; continue; }
            if (!a.isPlaying) a.Play();
            if (Mathf.Abs(a.time - local) > 0.06f)
                a.timeSamples = Mathf.Clamp((int)(local * clip.frequency), 0, clip.samples - 1);
            a.pitch = pitch;
            if (newMaster < 0) newMaster = s;
        }
        masterStem = newMaster;
    }

    int masterStem = -1;   // 手回し中に「今の位置」の基準にする段

    void ExitCrank()
    {
        Gamepad.current?.ResetHaptics();
        rumbleUntil = 0f;
        passTimes.Clear();
        int p = pendingPage >= 0 ? pendingPage : currentPage;
        float songSec = (float)QToSongSec(crankQ);
        if (p != currentPage) StartPageNow(p);
        else StartPageFrom(p, songSec);
    }

    // =====================================================================
    // ループの余韻（簡単なリバーブを、区切りの直前の音から計算して作る）
    // =====================================================================

    AudioClip GetTail(int page, int s, AudioClip clip, float ws, float we)
    {
        long key = ((long)page << 16) | (uint)s;
        if (tailCache.TryGetValue(key, out var cached)) return cached;

        int freq = clip.frequency, ch = clip.channels;
        float inSec = 0.6f;                              // 区切りの直前0.6秒の音を残響に通す
        int inFrames = Mathf.Max(1, (int)(inSec * freq));
        int startFrame = Mathf.Clamp((int)(we * freq) - inFrames, 0, clip.samples - 1);
        inFrames = Mathf.Min(inFrames, clip.samples - startFrame);
        int tailFrames = Mathf.Max(1, (int)(tailSeconds * freq));

        var input = new float[inFrames * ch];
        if (inFrames <= 0 || !clip.GetData(input, startFrame)) { tailCache[key] = null; return null; }

        float peak = 0f;
        foreach (var v in input) peak = Mathf.Max(peak, Mathf.Abs(v));
        if (peak < 0.0005f) { tailCache[key] = null; return null; }   // 無音なら余韻もいらない

        var output = new float[tailFrames * ch];
        float[] combMs = { 29.7f, 37.1f, 41.1f, 43.7f };
        float[] apMs = { 5.0f, 1.7f };
        for (int c = 0; c < ch; c++)
        {
            float spread = c * 1.3f;   // 左右で少しずらして広がりを出す
            var combs = new float[combMs.Length][];
            var idx = new int[combMs.Length];
            var fb = new float[combMs.Length];
            var store = new float[combMs.Length];
            for (int i = 0; i < combMs.Length; i++)
            {
                float d = (combMs[i] + spread) / 1000f;
                combs[i] = new float[Mathf.Max(1, (int)(d * freq))];
                fb[i] = Mathf.Pow(10f, -3f * d / Mathf.Max(0.1f, tailSeconds));
            }
            var aps = new float[apMs.Length][];
            var apIdx = new int[apMs.Length];
            for (int i = 0; i < apMs.Length; i++) aps[i] = new float[Mathf.Max(1, (int)((apMs[i] + spread * 0.3f) / 1000f * freq))];

            int total = inFrames + tailFrames;
            for (int n = 0; n < total; n++)
            {
                float x = n < inFrames ? input[n * ch + c] : 0f;
                float y = 0f;
                for (int i = 0; i < combs.Length; i++)
                {
                    float o = combs[i][idx[i]];
                    store[i] = o * (1f - tailDamping) + store[i] * tailDamping;
                    combs[i][idx[i]] = x + store[i] * fb[i];
                    idx[i] = (idx[i] + 1) % combs[i].Length;
                    y += o;
                }
                y *= 0.25f;
                for (int i = 0; i < aps.Length; i++)
                {
                    float b = aps[i][apIdx[i]];
                    float o = -0.7f * y + b;
                    aps[i][apIdx[i]] = y + 0.7f * b;
                    apIdx[i] = (apIdx[i] + 1) % aps[i].Length;
                    y = o;
                }
                int k = n - inFrames;
                if (k < 0) continue;
                // 終わりに向けて確実に0へ
                float env = 1f;
                float fadeStart = tailFrames * 0.6f;
                if (k > fadeStart) env = 1f - (k - fadeStart) / (tailFrames - fadeStart);
                output[k * ch + c] = y * tailLevel * env;
            }
        }

        var tail = AudioClip.Create(stems[s].name + "_tail_p" + (page + 1), tailFrames, ch, freq, false);
        tail.SetData(output, 0);
        tailCache[key] = tail;
        return tail;
    }

    Vector3 lastTailParams;

    // インスペクターで余韻の設定を変えたら、作り直す
    void CheckTailParams()
    {
        var cur = new Vector3(tailSeconds, tailLevel, tailDamping);
        if (cur != lastTailParams) { lastTailParams = cur; ClearTails(); }
    }

    void ClearTails()
    {
        foreach (var kv in tailCache) if (kv.Value != null) Destroy(kv.Value);
        tailCache.Clear();
    }

    // =====================================================================
    // 操作
    // =====================================================================

    void HandleInput()
    {
        var gp = Gamepad.current;
        var kb = Keyboard.current;

        ButtonControl aBtn = null;
        if (gp != null) aBtn = buttonLayout == ButtonLayout.Xbox ? gp.buttonSouth : gp.buttonEast;

        bool next = (gp != null && controllerPageButtons && gp.rightShoulder.wasPressedThisFrame) || (kb != null && kb.eKey.wasPressedThisFrame);
        bool prev = (gp != null && controllerPageButtons && gp.leftShoulder.wasPressedThisFrame) || (kb != null && kb.qKey.wasPressedThisFrame);
        bool up = (gp != null && gp.dpad.up.wasPressedThisFrame) || (kb != null && kb.upArrowKey.wasPressedThisFrame);
        bool down = (gp != null && gp.dpad.down.wasPressedThisFrame) || (kb != null && kb.downArrowKey.wasPressedThisFrame);
        bool toggle = (aBtn != null && aBtn.wasPressedThisFrame) || (kb != null && kb.spaceKey.wasPressedThisFrame)
                      || (gp != null && gp.rightStickButton.wasPressedThisFrame);

        // 右スティックの上下で段を選ぶ（倒した瞬間に1つ、倒しっぱなしなら少し待ってから続けて動く）
        if (gp != null)
        {
            float ry = gp.rightStick.ReadValue().y;
            int rdir = ry > 0.6f ? -1 : (ry < -0.6f ? 1 : 0);
            if (rdir == 0)
            {
                if (Mathf.Abs(ry) < 0.3f) rsDir = 0;
            }
            else if (rdir != rsDir)
            {
                rsDir = rdir;
                rsRepeatAt = Time.unscaledTime + 0.4f;
                MoveSelection(rdir);
            }
            else if (Time.unscaledTime >= rsRepeatAt)
            {
                rsRepeatAt = Time.unscaledTime + 0.15f;
                MoveSelection(rdir);
            }
        }
        bool edit = (gp != null && gp.startButton.wasPressedThisFrame) || (kb != null && kb.tabKey.wasPressedThisFrame);

        bool rsMoved = gp != null && gp.rightStick.ReadValue().magnitude > 0.5f;
        if (next || prev || up || down || toggle || edit || rsMoved || (kb != null && kb.anyKey.wasPressedThisFrame))
            lastInputTime = Time.unscaledTime;

        if (kb != null && kb.f1Key.wasPressedThisFrame) showDebugPanel = !showDebugPanel;
        if (kb != null && kb.f2Key.wasPressedThisFrame) showAudioDebug = !showAudioDebug;
        if (edit) SetEditMode(!editMode);
        // ページボタンは、押した直後の0.35秒は受け付けない（スティックを回している間に、1回の押しが2回に数えられることがあるため）
        if ((next || prev) && Time.unscaledTime - lastPageButtonTime < 0.35f) { next = false; prev = false; }
        // 待機中（最初のページの繰り返し）は、スティックを回しはじめるまでページをめくれない
        if (attractLoop && !editMode && !debugHold) { next = false; prev = false; }
        if (next || prev) lastPageButtonTime = Time.unscaledTime;
        if (next) RequestPage(+1);
        if (prev) RequestPage(-1);
        if (up) MoveSelection(-1);
        if (down) MoveSelection(+1);
        if (toggle && shownPage >= 0 && rowVisible[shownPage, selectedStem])
            stems[selectedStem].mute = !stems[selectedStem].mute;

        if (editMode && kb != null && (kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame))
        {
            var pg = pages[shownPage];
            var w = FindWindow(pg, selectedStem);
            if (w != null)
            {
                pg.stemWindows.Remove(w);
                CommitEdit();
            }
        }
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

    float lastPageButtonTime = -10f;

    // 予約は「今のページの1つ先（または1つ前）」だけ。何回押しても2ページ以上は飛ばない
    void RequestPage(int delta)
    {
        // 手回し中は、自動再生のときの予約（upcomingPage）は使わない
        int decided = !cranking && nextDecided ? upcomingPage : currentPage;
        int target = Mathf.Clamp(decided + delta, 0, pages.Count - 1);
        pendingPage = target == decided ? -1 : target;
        Debug.Log("ページ予約：" + (currentPage + 1) + " → " + (pendingPage >= 0 ? (pendingPage + 1).ToString() : "なし") + (cranking ? "（手回し中）" : ""));
    }

    // =====================================================================
    // 設定モード
    // =====================================================================

    void SetEditMode(bool on)
    {
        editMode = on;
        dragKind = 0;
        editRoot.gameObject.SetActive(on);
        RebuildPage(shownPage);
        ShowPage(shownPage);
        if (on) Debug.Log("設定モード：帯の端をドラッグして範囲を調整できます（Tabで終了）");
    }

    float SnapStep()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.ctrlKey.isPressed) return 0.25f;
        if (kb != null && kb.shiftKey.isPressed) return 0.5f;
        return Mathf.Max(0.0625f, snapBeats);
    }

    float QToX(double q, int p) => RowsX + (float)((q - axisQ0[p]) / (axisQ1[p] - axisQ0[p])) * RowsW;
    double XToQ(float x, int p) => axisQ0[p] + (x - RowsX) / RowsW * (axisQ1[p] - axisQ0[p]);

    void HandleEditMouse()
    {
        var m = Mouse.current;
        if (m == null || uiCanvas == null) return;
        int p = shownPage;
        var pg = pages[p];
        float sf = Mathf.Max(0.0001f, uiCanvas.scaleFactor);
        Vector2 mp = m.position.ReadValue();
        float cx = mp.x / sf, cy = (Screen.height - mp.y) / sf;

        if (m.leftButton.wasPressedThisFrame)
        {
            dragKind = 0;
            if (cy >= PageRowY && cy <= PageRowY + PageRowH)
            {
                if (Mathf.Abs(cx - QToX(PageStartQ(pg), p)) <= 12f) dragKind = 1;
                else if (Mathf.Abs(cx - QToX(PageEndQ(pg), p)) <= 12f) dragKind = 2;
            }
            else
            {
                for (int s = 0; s < stems.Count; s++)
                {
                    if (rowY[s] < 0f || cy < rowY[s] || cy > rowY[s] + curRowH) continue;
                    selectedStem = s;
                    GetWindowQ(pg, s, out double q0, out double q1);
                    if (Mathf.Abs(cx - QToX(q0, p)) <= 12f) { dragKind = 3; dragStem = s; }
                    else if (Mathf.Abs(cx - QToX(q1, p)) <= 12f) { dragKind = 4; dragStem = s; }
                    break;
                }
            }
        }

        if (dragKind != 0 && m.leftButton.isPressed)
        {
            float step = SnapStep();
            double q = XToQ(cx, p);
            q = Math.Round(q / step) * step;
            q = Math.Max(axisQ0[p], Math.Min(axisQ1[p], q));

            double ps = PageStartQ(pg), pe = PageEndQ(pg);
            switch (dragKind)
            {
                case 1: SetPageRange(pg, Math.Min(q, pe - step), pe); break;
                case 2: SetPageRange(pg, ps, Math.Max(q, ps + step)); break;
                case 3:
                {
                    GetWindowQ(pg, dragStem, out _, out double q1);
                    GetOrAddWindow(pg, dragStem).startOffsetBeats = (float)(Math.Min(q, q1 - step) - ps);
                    break;
                }
                case 4:
                {
                    GetWindowQ(pg, dragStem, out double q0, out _);
                    GetOrAddWindow(pg, dragStem).endOffsetBeats = (float)(Math.Max(q, q0 + step) - pe);
                    break;
                }
            }
            editsChanged = true;
        }

        if (dragKind != 0 && m.leftButton.wasReleasedThisFrame)
        {
            dragKind = 0;
            if (editsChanged) CommitEdit();
        }
    }

    // ページの範囲を拍で指定する（小節番号＋拍のずれに分けて保存）
    void SetPageRange(PoemPage pg, double qs, double qe)
    {
        pg.startBar = Math.Max(1, (int)Math.Floor(qs / Bpb + 1e-6) + 1);
        pg.startBeatOffset = (float)(qs - (pg.startBar - 1) * (double)Bpb);
        double len = qe - (pg.startBar - 1) * (double)Bpb;
        pg.lengthBars = Math.Max(1, (int)Math.Floor(len / Bpb + 1e-6));
        pg.endBeatOffset = (float)(len - pg.lengthBars * (double)Bpb);
    }

    // 調整を確定：絵を描き直し、ページを頭から鳴らし直し、ファイルに保存
    void CommitEdit()
    {
        editsChanged = false;
        ClearTails();
        int p = shownPage;
        RebuildPage(p);
        ShowPage(p);
        StartPageNow(p);
        SaveEditsFile();
    }

    void UpdateEditOverlay()
    {
        int p = shownPage;
        var pg = pages[p];
        double ps = PageStartQ(pg), pe = PageEndQ(pg);

        SetBand(pageBand, pageH0, pageH1, QToX(ps, p), QToX(pe, p), PageRowY, PageRowH);
        pageRangeText.text = "ページ範囲　" + BarBeat(ps) + " 〜 " + BarBeat(pe);

        for (int s = 0; s < stems.Count; s++)
        {
            bool on = rowY[s] >= 0f;
            bands[s].enabled = on;
            bandH0[s].enabled = on;
            bandH1[s].enabled = on;
            if (!on) continue;
            GetWindowQ(pg, s, out double q0, out double q1);
            SetBand(bands[s], bandH0[s], bandH1[s], QToX(q0, p), QToX(q1, p), rowY[s], curRowH);
            float a = s == selectedStem ? 0.16f : 0.07f;
            bands[s].color = new Color(editColor.r, editColor.g, editColor.b, a);
        }

        {
            GetWindowQ(pg, selectedStem, out double q0, out double q1);
            editInfoText.text = stems[selectedStem].name + "：" + BarBeat(q0) + " 〜 " + BarBeat(q1) +
                                "（ページから " + FormatShift(q0 - ps) + " ／ " + FormatShift(q1 - pe) + "）";
        }
    }

    static string FormatShift(double beats)
    {
        if (Math.Abs(beats) < 1e-4) return "ずれなし";
        return (beats > 0 ? "+" : "") + beats.ToString("0.##") + "拍";
    }

    void SetBand(Image band, Image h0, Image h1, float x0, float x1, float y, float h)
    {
        float l = Mathf.Clamp(x0, RowsX, RowsX + RowsW), r = Mathf.Clamp(x1, RowsX, RowsX + RowsW);
        SetRect(band.rectTransform, l, y, Mathf.Max(0f, r - l), h);
        SetRect(h0.rectTransform, x0 - HandleW * 0.5f, y, HandleW, h);
        SetRect(h1.rectTransform, x1 - HandleW * 0.5f, y, HandleW, h);
    }

    // =====================================================================
    // 波形とMIDIラインの画像
    // =====================================================================

    List<MidiNote> GetNotes(PoemStem st)
    {
        var src = SelectNotes(st);
        if (src == null) return null;
        // 途中から書き出したMIDIは、その小節のぶん後ろにずらしてから秒に直す
        double offQ = (Mathf.Max(1, st.midiStartBar) - 1) * (double)Bpb;
        var list = new List<MidiNote>(src.Count);
        foreach (var n in src)
            list.Add(new MidiNote
            {
                q0 = n.q0 + offQ,
                q1 = n.q1 + offQ,
                pitch = n.pitch,
                start = (float)QToSongSec(n.q0 + offQ) + midiOffsetSec,
                end = (float)QToSongSec(n.q1 + offQ) + midiOffsetSec
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

    void BuildAllTextures()
    {
        int P = pages.Count, S = stems.Count;
        notesCache = new List<MidiNote>[S];
        for (int s = 0; s < S; s++) notesCache[s] = GetNotes(stems[s]);

        axisQ0 = new double[P];
        axisQ1 = new double[P];
        rowVisible = new bool[P, S];
        texEdit = new bool[P];
        rowTex = new Texture2D[P, S];
        stemMax = new float[S];

        // 音量の物差しは曲全体で揃える（ページによって波形の大きさが変わらないように）
        var peaks = new float[P][][];
        for (int p = 0; p < P; p++)
        {
            ComputeLayout(p);
            peaks[p] = new float[S][];
            for (int s = 0; s < S; s++)
            {
                peaks[p][s] = PeaksFor(s, p);
                if (peaks[p][s] != null) foreach (float v in peaks[p][s]) stemMax[s] = Mathf.Max(stemMax[s], v);
            }
        }
        for (int p = 0; p < P; p++) DrawPage(p, peaks[p]);
    }

    // 1ページぶんだけ描き直す（設定モード用）
    void RebuildPage(int p)
    {
        int S = stems.Count;
        ComputeLayout(p);
        var pk = new float[S][];
        for (int s = 0; s < S; s++)
        {
            pk[s] = PeaksFor(s, p);
            if (pk[s] != null) foreach (float v in pk[s]) stemMax[s] = Mathf.Max(stemMax[s], v);
            if (rowTex[p, s] != null) Destroy(rowTex[p, s]);
        }
        DrawPage(p, pk);
    }

    // ページの横軸の範囲と、表示する段を決める
    void ComputeLayout(int p)
    {
        var pg = pages[p];
        double q0 = double.MaxValue, q1 = double.MinValue;
        for (int s = 0; s < stems.Count; s++)
        {
            GetWindowQ(pg, s, out double a, out double b);
            q0 = Math.Min(q0, a);
            q1 = Math.Max(q1, b);

            var notes = notesCache[s];
            bool hasMidi = notes != null && notes.Count > 0;
            bool visible = stems[s].clip != null || hasMidi;
            if (visible && hasMidi && stems[s].hideWhenNoNotes)
            {
                GetWindow(pg, s, out float ws, out float we);
                if (!HasNotesIn(notes, ws, we)) visible = false;
            }
            rowVisible[p, s] = visible;
        }
        if (editMode)
        {
            // 設定モードでは、ページの前後1小節ぶん広く見せる（範囲を広げられるように）
            q0 = Math.Min(q0, PageStartQ(pg) - Bpb);
            q1 = Math.Max(q1, PageEndQ(pg) + Bpb);
        }
        q0 = Math.Max(q0, SongSecToQ(0));
        if (q1 <= q0) q1 = q0 + Bpb;
        axisQ0[p] = q0;
        axisQ1[p] = q1;
        texEdit[p] = editMode;
    }

    // 段 s のページ p での波形（ふだんは鳴る範囲だけ、設定モードでは見えている範囲すべて）
    float[] PeaksFor(int s, int p)
    {
        GetWindowQ(pages[p], s, out double r0, out double r1);
        if (editMode) { r0 = axisQ0[p]; r1 = axisQ1[p]; }
        return ComputePeaks(s, r0, r1, axisQ0[p], axisQ1[p]);
    }

    float[] ComputePeaks(int s, double r0, double r1, double ax0, double ax1)
    {
        var clip = stems[s].clip;
        if (clip == null) return null;
        float off = ClipOffset(s);
        float ws = Mathf.Max(0f, (float)QToSongSec(r0) - off);
        float we = Mathf.Min((float)QToSongSec(r1) - off, clip.length);
        if (we <= ws) return null;

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
            float t0 = (float)QToSongSec(ax0 + (ax1 - ax0) * x / TexW) - off;
            float t1 = (float)QToSongSec(ax0 + (ax1 - ax0) * (x + 1) / TexW) - off;
            int f0 = Mathf.Clamp((int)((t0 - ws) * freq), 0, frames);
            int f1 = Mathf.Clamp((int)((t1 - ws) * freq), 0, frames);
            if (f1 <= f0) continue;
            int stride = Mathf.Max(1, (f1 - f0) / 256);
            float mx = 0f;
            for (int f = f0; f < f1; f += stride)
                for (int c = 0; c < ch; c++)
                    mx = Mathf.Max(mx, Mathf.Abs(buf[f * ch + c]));
            result[x] = mx;
        }
        return result;
    }

    void DrawPage(int p, float[][] pk)
    {
        int S = stems.Count;
        var pg = pages[p];

        // メロディとハモリの音の高さが比べられるよう、ページ内のMIDIの段は同じ音域の物差しで描く
        int lo = 127, hi = 0;
        for (int s = 0; s < S; s++)
        {
            var notes = notesCache[s];
            if (notes == null || !rowVisible[p, s]) continue;
            ReadRange(pg, s, p, out double r0, out double r1);
            foreach (var n in notes)
            {
                double nq0 = SongSecToQ(n.start), nq1 = SongSecToQ(n.end);
                if (nq1 > r0 && nq0 < r1) { lo = Mathf.Min(lo, n.pitch); hi = Mathf.Max(hi, n.pitch); }
            }
        }
        if (hi < lo) { lo = 60; hi = 72; }
        if (hi - lo < 6) { int c = (lo + hi) / 2; lo = c - 3; hi = c + 3; }

        for (int s = 0; s < S; s++)
        {
            var px = new Color32[TexW * TexH];
            bool isMidiRow = notesCache[s] != null && notesCache[s].Count > 0;
            byte alpha = (byte)(255 * (isMidiRow ? midiRowWaveformAlpha : 1f));
            if (pk[s] != null && stemMax[s] > 0f && alpha > 0)
                DrawWaveform(px, pk[s], stemMax[s], alpha);
            if (isMidiRow)
            {
                ReadRange(pg, s, p, out double r0, out double r1);
                DrawMidi(px, notesCache[s], r0, r1, axisQ0[p], axisQ1[p], lo, hi);
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

    void ReadRange(PoemPage pg, int s, int p, out double r0, out double r1)
    {
        GetWindowQ(pg, s, out r0, out r1);
        if (texEdit[p]) { r0 = axisQ0[p]; r1 = axisQ1[p]; }
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

    void DrawMidi(Color32[] px, List<MidiNote> notes, double r0, double r1, double ax0, double ax1, int lo, int hi)
    {
        float margin = TexH * 0.1f;
        int X(double q) => Mathf.RoundToInt((float)((q - ax0) / (ax1 - ax0)) * (TexW - 1));
        int Y(int pitch) => Mathf.RoundToInt(margin + (float)(pitch - lo) / (hi - lo) * (TexH - margin * 2f));

        bool hasPrev = false;
        double prevQ1 = 0;
        int prevPitch = 0;
        foreach (var n in notes)
        {
            double nq0 = SongSecToQ(n.start), nq1 = SongSecToQ(n.end);
            if (nq1 <= r0 || nq0 >= r1) continue;
            double s = Math.Max(nq0, r0), e = Math.Min(nq1, r1);
            if (hasPrev && nq0 - prevQ1 < 1.0)
                ThickLine(px, X(Math.Min(prevQ1, r1)), Y(prevPitch), X(s), Y(n.pitch), 2 * Thick);
            ThickLine(px, X(s), Y(n.pitch), X(e), Y(n.pitch), 4 * Thick);
            hasPrev = true;
            prevQ1 = nq1;
            prevPitch = n.pitch;
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

    // =====================================================================
    // MIDIファイルの読み取り（標準MIDIファイルの最小限の解析）
    // =====================================================================

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

    // =====================================================================
    // 画面の組み立て
    // =====================================================================

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
        uiCanvas = cgo.AddComponent<Canvas>();
        uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
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

        // 水彩の色：紙のすぐ上、歌詞や波形の下に重ねる
        var washShader = Shader.Find("Hidden/WatercolorWash");
        if (washShader != null)
        {
            var wrt = Place("Wash", root, 0f, 0f, 0f, 0f);
            wrt.anchorMin = Vector2.zero;
            wrt.anchorMax = Vector2.one;
            wrt.offsetMin = wrt.offsetMax = Vector2.zero;
            washImg = wrt.gameObject.AddComponent<RawImage>();
            washImg.raycastTarget = false;
            washMat = new Material(washShader);
            washImg.material = washMat;
            washImg.enabled = false;
            washCur = washColor;
        }
        else if (washEnabled) Debug.LogWarning("WatercolorWash.shader が見つからないので、水彩の色はオフになります。Assets に入れてください。");

        BuildUIParts(root);

        lyricImg = Place("Lyric", root, LyricX, LyricY, LyricW, LyricH).gameObject.AddComponent<RawImage>();
        lyricImg.raycastTarget = false;
        var blurShader = Shader.Find("Hidden/LyricBlur");
        if (blurShader != null)
        {
            lyricMat = new Material(blurShader);
            lyricImg.material = lyricMat;
        }
        else if (lyricFocus) Debug.LogWarning("LyricBlur.shader が見つからないので、歌詞のピントの演出はオフになります。Assets に入れてください。");
        lyricText = MakeText(root, "LyricText", LyricX, LyricY, LyricW, LyricH, 52, TextAnchor.MiddleCenter);

        // 段の背景（選択中のハイライト）は小節線より下に置く
        var highlightRoot = Place("Highlights", root, 0f, 0f, 1920f, 1080f);
        gridRoot = Place("Grid", root, RowsX, RowsTop, RowsW, RowsBottom - RowsTop);

        rowY = new float[stems.Count];
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

        // 設定モードの帯とつまみ
        editRoot = Place("EditOverlay", root, 0f, 0f, 1920f, 1080f);
        var handleCol = new Color(editColor.r, editColor.g, editColor.b, 0.9f);
        pageBand = MakeImage(editRoot, "PageBand", 0f, PageRowY, 10f, PageRowH, new Color(inkColor.r, inkColor.g, inkColor.b, 0.10f));
        pageH0 = MakeImage(editRoot, "PageStart", 0f, PageRowY, HandleW, PageRowH, new Color(inkColor.r, inkColor.g, inkColor.b, 0.85f));
        pageH1 = MakeImage(editRoot, "PageEnd", 0f, PageRowY, HandleW, PageRowH, new Color(inkColor.r, inkColor.g, inkColor.b, 0.85f));
        pageRangeText = MakeText(editRoot, "PageRange", 60f, PageRowY, RowsX - 70f, PageRowH, 16, TextAnchor.MiddleLeft);
        for (int s = 0; s < stems.Count; s++)
        {
            bands.Add(MakeImage(editRoot, "Band" + s, 0f, 0f, 10f, 10f, new Color(editColor.r, editColor.g, editColor.b, 0.07f)));
            bandH0.Add(MakeImage(editRoot, "BandStart" + s, 0f, 0f, HandleW, 10f, handleCol));
            bandH1.Add(MakeImage(editRoot, "BandEnd" + s, 0f, 0f, HandleW, 10f, handleCol));
        }
        editInfoText = MakeText(editRoot, "EditInfo", 40f, 1044f, 1100f, 30f, 18, TextAnchor.MiddleLeft);
        editHelpText = MakeText(editRoot, "EditHelp", 1000f, 1044f, 880f, 30f, 16, TextAnchor.MiddleRight);
        editHelpText.text = "設定モード　端をドラッグ（Shift:半拍 Ctrl:1/4拍）　Delete:段のずらしを消す　Tab:戻る";
        editHelpText.color = editColor;
        editRoot.gameObject.SetActive(false);

        playhead = MakeImage(root, "Playhead", RowsX, RowsTop - 6f, 2f, 10f, playheadColor);

        pageText = MakeText(root, "PageInfo", 40f, 20f, 900f, 36f, 24, TextAnchor.UpperLeft);
        pendingText = MakeText(root, "Pending", 40f, 56f, 900f, 36f, 22, TextAnchor.UpperLeft);
        debugText = MakeText(root, "AudioDebug", 1020f, 20f, 860f, 36f, 20, TextAnchor.UpperRight);
        crankText = MakeText(root, "Crank", 560f, 18f, 800f, 40f, 26, TextAnchor.UpperCenter);

        // ページめくり用：いちばん上に重ねる画面いっぱいの絵
        var curlShader = Shader.Find("Hidden/PageCurl");
        if (curlShader != null)
        {
            var tgo = new GameObject("PageTurn");
            var tc = tgo.AddComponent<Canvas>();
            tc.renderMode = RenderMode.ScreenSpaceOverlay;
            tc.sortingOrder = 100;
            var trt = Place("Curl", tgo.transform, 0f, 0f, 0f, 0f);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            turnImg = trt.gameObject.AddComponent<RawImage>();
            turnImg.raycastTarget = false;
            turnMat = new Material(curlShader);
            turnImg.material = turnMat;
            turnImg.enabled = false;
        }
        else if (pageTurnAnim) Debug.LogWarning("PageCurl.shader が見つからないので、ページめくりのアニメーションはオフになります。Assets に入れてください。");
    }

    void ShowPage(int p)
    {
        if (texEdit[p] != editMode) RebuildPage(p);
        shownPage = p;
        var page = pages[p];

        if (page.lyricImage != null)
        {
            float w = page.lyricImage.width, h = page.lyricImage.height;
            float k = Mathf.Min(LyricW / w, LyricH / h) * lyricScale;
            SetRect(lyricImg.rectTransform, LyricX + (LyricW - w * k) * 0.5f, LyricY + (LyricH - h * k) * 0.5f, w * k, h * k);
            // ぼかしの大きさを「画面上のピクセル」でそろえるため、画像のピクセルに換算しておく
            if (lyricMat != null) lyricMat.SetFloat("_BlurPx", maxBlurPx / Mathf.Max(0.0001f, k));
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
            rowY[s] = -1f;
            if (on) visible.Add(s);
        }
        int n = Mathf.Max(1, visible.Count);
        float rowH = Mathf.Min(MaxRowH, (RowsBottom - RowsTop - RowGap * (n - 1)) / n);
        curRowH = rowH;
        int fontSize = Mathf.RoundToInt(Mathf.Min(26f, rowH * 0.4f));
        for (int i = 0; i < visible.Count; i++)
        {
            int s = visible[i];
            float y = RowsTop + i * (rowH + RowGap);
            rowY[s] = y;
            SetRect(rowRoots[s], 0f, y, 1920f, rowH);
            SetRect(markers[s].rectTransform, 60f, 0f, 40f, rowH);
            SetRect(labels[s].rectTransform, 100f, 0f, RowsX - 110f, rowH);
            SetRect(waveImgs[s].rectTransform, RowsX, 0f, RowsW, rowH);
            markers[s].fontSize = fontSize;
            labels[s].fontSize = fontSize;
            SetRect(highlights[s].rectTransform, 50f, y - 4f, RowsX + RowsW - 30f, rowH + 8f);

            Texture2D custom = !editMode && s < page.stemImageOverrides.Count ? page.stemImageOverrides[s] : null;
            waveImgs[s].texture = custom != null ? custom : rowTex[p, s];
        }
        float rowsH = Mathf.Max(10f, visible.Count * (rowH + RowGap) - RowGap);

        if (!rowVisible[p, selectedStem] && visible.Count > 0) selectedStem = visible[0];

        // 小節線（設定モードでは拍の線と小節番号も）
        for (int i = gridRoot.childCount - 1; i >= 0; i--) Destroy(gridRoot.GetChild(i).gameObject);
        SetRect(gridRoot, RowsX, RowsTop, RowsW, rowsH);
        double a0 = axisQ0[p], a1 = axisQ1[p];
        var barCol = new Color(inkColor.r, inkColor.g, inkColor.b, editMode ? 0.22f : 0.12f);
        var beatCol = new Color(inkColor.r, inkColor.g, inkColor.b, 0.06f);
        long k0 = (long)Math.Ceiling(a0 - 1e-6), k1 = (long)Math.Floor(a1 + 1e-6);
        for (long k = k0; k <= k1; k++)
        {
            bool isBar = k % Bpb == 0;
            if (!isBar && (!editMode || k1 - k0 > 400)) continue;
            float x = (float)((k - a0) / (a1 - a0)) * RowsW;
            MakeImage(gridRoot, isBar ? "Bar" : "Beat", x - (isBar ? 1f : 0.5f), 0f, isBar ? 2f : 1f, rowsH, isBar ? barCol : beatCol);
            if (isBar && editMode)
            {
                var t = MakeText(gridRoot, "BarNo", x + 4f, -(RowsTop - PageRowY), 60f, PageRowH, 14, TextAnchor.MiddleLeft);
                t.text = (k / Bpb + 1).ToString();
                t.color = new Color(inkColor.r, inkColor.g, inkColor.b, 0.5f);
            }
        }
        playhead.rectTransform.sizeDelta = new Vector2(2f, rowsH + 12f);

        pageText.text = (editMode ? "【設定モード】　" : "") + "ページ " + (p + 1) + " / " + pages.Count + "　" + page.title;
    }

    void UpdateVisuals(double now)
    {
        UpdateWash();
        UpdateUIParts(now);
        // 確認用の文字は、まとめて表示・非表示（設定モード中はページ番号だけ出す）
        pageText.enabled = (showAudioDebug && showPageNumber) || editMode;
        pendingText.enabled = showAudioDebug;
        crankText.enabled = showAudioDebug;
        debugText.enabled = showAudioDebug;

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

        // 歌詞のピント：再生が1.00倍（ふつうの再生、または手回しでぴったり）のときだけ合う
        {
            float target = 0f;
            if (lyricFocus && cranking && !crankLocked && !atEnd)
            {
                double natural = 1.0 / Math.Max(1e-4, SecPerQAt(crankQ));
                double dev = Math.Abs(crankRate / natural - 1.0);
                target = Mathf.Clamp01((float)(dev / Math.Max(0.01, blurAtDeviation)));
            }
            // 歌詞の段（メロディ）をミュートしているときは、歌詞がぼける
            int ls = FindStem(lyricStemName);
            bool lyricMuted = ls >= 0 && stems[ls].mute;
            if (lyricMuted) target = 1f;
            lyricBlur = Mathf.Lerp(lyricBlur, target, 1f - Mathf.Exp(-focusSpeed * Time.unscaledDeltaTime));
            if (lyricMat != null) lyricMat.SetFloat("_Blur", lyricBlur);
            lyricImg.color = new Color(1f, 1f, 1f, Mathf.Lerp(1f, lyricMuted ? 0.5f : 0.75f, lyricBlur));
        }

        if (cranking && atEnd && !rewinding)
        {
            crankText.text = "― おわり ―";
            crankText.color = inkColor;
        }
        else if (cranking && rewinding)
        {
            crankText.text = "◀◀ 巻き戻し中";
            crankText.color = inkColor;
        }
        else if (cranking)
        {
            if (crankMode == CrankMode.テンポ)
            {
                double natural = 1.0 / Math.Max(1e-4, SecPerQAt(crankQ));
                double ratio = crankRate / natural;
                crankText.text = crankLocked ? "◎ ぴったり（" + (ratio < 0 ? "逆回し " : "") + "1.0倍）" : "テンポ " + stepRatio.ToString("0.0") + "倍";
                crankText.color = crankLocked ? playheadColor : inkColor;
            }
            else
            {
                crankText.text = "手回し";
                crankText.color = inkColor;
            }
        }
        else crankText.text = "";

        int waiting = pendingPage >= 0 ? pendingPage : (!cranking && nextDecided && upcomingPage != currentPage ? upcomingPage : -1);
        pendingText.text = waiting >= 0 ? "→ ページ " + (waiting + 1) + " へ（このフレーズを読み終えてから）" : "";

        // 赤い線：今鳴っている位置（横軸は拍なので、テンポが遅いところではゆっくり進む）
        int p = shownPage;
        var page = pages[currentPage];
        double iterStart = nextIterStart - PageLen(currentPage);
        double songT = cranking ? QToSongSec(crankQ) : PageStartSec(page) + (now - iterStart);
        double span = axisQ1[p] - axisQ0[p];
        float frac = currentPage == p && span > 0 ? (float)((SongSecToQ(songT) - axisQ0[p]) / span) : -1f;
        playhead.enabled = frac >= 0f && frac <= 1f;
        var rt = playhead.rectTransform;
        rt.anchoredPosition = new Vector2(RowsX + frac * RowsW - 1f, -(RowsTop - 6f));
    }
}
