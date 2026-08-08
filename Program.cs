using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

class Program
{
    // ===== IBD Market Pulse ロジックのパラメータ =====
    // 売り抜け日(Distribution Day)判定の下落率しきい値
    const decimal DIST_DAY_DROP_PCT = 0.2m;
    // 売り抜け日が有効とみなされる期間（営業日）
    const int DIST_DAY_WINDOW = 25;
    // 売り抜け日が「失効（無効化）」する反発率（その日の終値からの上昇率）
    const decimal DIST_DAY_INVALIDATE_RALLY_PCT = 5.0m;
    // Under Pressure と判定する売り抜け日数のしきい値
    const int DIST_DAY_PRESSURE_THRESHOLD = 5;
    // Confirmed Uptrend から Correction へ「格下げ」する際に必要な売り抜け日数
    const int DIST_DAY_BREAKDOWN_THRESHOLD = 6;
    // フォロースルーデー(Follow-Through Day)の上昇率しきい値
    const decimal FTD_GAIN_PCT = 1.25m;
    // ラリー・アテンプト開始から何営業日目以降にFTDを認めるか（"Day 4"以降）
    const int FTD_MIN_DAY = 3; // day1Index からの経過日数（0始まり）
    // ラリー・アテンプトが有効な最大期間（これを超えたらリセットして再探索）
    const int FTD_MAX_DAY = 25;
    // Day1（ラリー起点）判定に使う直近安値の参照期間
    const int DAY1_LOOKBACK = 10;

    // ===== Put/Call Ratio（自前算出）のパラメータ =====
    // IBD/CBOEの公式値とは母集団が異なる代替指標。SPYオプションの出来高から独自算出する。
    const string PUT_CALL_UNDERLYING = "SPY";
    // トレンド把握用の移動平均日数
    const int PUT_CALL_SMA_WINDOW = 10;
    // パーセンタイル順位を意味のある値として表示し始める最低履歴日数
    const int PUT_CALL_MIN_HISTORY_FOR_PERCENTILE = 10;

    // ===== セクターローテーション（CAN SLIMの"L=Leader"に対応する補助指標） =====
    // 主要セクターの相対強度を見るためのSPDRセクターETF一覧
    static readonly Dictionary<string, string> SECTOR_ETFS = new()
    {
        ["XLK"] = "テクノロジー",
        ["XLF"] = "金融",
        ["XLE"] = "エネルギー",
        ["XLV"] = "ヘルスケア",
        ["XLY"] = "一般消費財",
        ["XLP"] = "生活必需品",
        ["XLI"] = "資本財",
        ["XLB"] = "素材",
        ["XLU"] = "公益事業",
        ["XLRE"] = "不動産",
        ["XLC"] = "通信サービス"
    };
    const int SECTOR_RETURN_1M_DAYS = 21; // 約1ヶ月分の営業日
    const int SECTOR_RETURN_3M_DAYS = 63; // 約3ヶ月分の営業日

    // ===== ディフェンシブ／シクリカル・ローテーション =====
    // 11本のセクター表を睨まなくても、資金が守りに回ったかを1つの差分で読む。
    static readonly string[] DEFENSIVE_SECTORS = { "XLP", "XLU", "XLV" };
    static readonly string[] CYCLICAL_SECTORS = { "XLY", "XLK", "XLI" };

    // ===== 市場リスクスコアの事後検証 =====
    // スコアが記録された21/63営業日後の実績を確定値として保存する。
    // 当日の終値からの途中経過ではなく、指定営業日後の終値を使うことで検証値が後から変わらないようにする。
    const int SCORE_VALIDATION_1M_DAYS = 21;
    const int SCORE_VALIDATION_3M_DAYS = 63;
    const int SCORE_VALIDATION_RECOMMENDED_MIN_SAMPLES = 10;

    // ===== 値幅代理指標（Breadth Proxies） =====
    // 「真のブレッドス」とは別の補助指標。時価総額集中・小型株の強弱を素早く確認するために残す。
    // RSP: S&P500均等加重（時価総額加重のSPYとの差が集中度の目安）
    // IWM: 小型株(Russell 2000)。景気敏感・高ベータでリスク選好の代理指標になる
    static readonly Dictionary<string, string> BREADTH_PROXY_ETFS = new()
    {
        ["RSP"] = "S&P500均等加重",
        ["IWM"] = "小型株(Russell 2000)"
    };

    // ===== Credit Risk Appetite（クレジット市場のリスク選好度） =====
    // HYG(ハイイールド社債ETF) と LQD(投資適格社債ETF) の相対リターン。
    // TLTとの比較は金利デュレーション差の影響が強いため、信用リスクの比較対象としては使わない。
    const string CREDIT_RISK_ON_SYMBOL = "HYG";
    const string CREDIT_RISK_OFF_SYMBOL = "LQD";
    const string HY_OAS_FRED_SERIES = "BAMLH0A0HYM2";

    // ===== ボラティリティ警戒灯 =====
    // VIX3Mは3ヶ月先のS&P 500インプライド・ボラティリティ指数。
    // VIX先物そのものではないが、日次の無料データで期限構造を確認する実用的な近似として用いる。
    const string VIX_SYMBOL = "^VIX";
    const string VIX3M_SYMBOL = "^VIX3M";
    const int VIX_SMA_WINDOW = 20;
    // Yahooの^VIX3M/^VIX9D/^VIX6Mは配信が数週間止まることがある（^VIXと^VVIXは継続配信）。
    // 期限構造が取れないだけでボラティリティ配点15点を丸ごと欠落させないよう、
    // SPY自身の短期／長期実現ボラティリティ比を代替の期限構造として使う。
    const int REALIZED_VOL_SHORT_WINDOW = 10;
    const int REALIZED_VOL_LONG_WINDOW = 63;
    const int TRADING_DAYS_PER_YEAR = 252;

    // ===== Nasdaq-100 真の市場ブレッドス =====
    // 構成銘柄スナップショットは公式Nasdaq資料（2026-05-01時点）に基づく。
    // 年次リバランスなどで構成が変わるため、nasdaq100-universe.txt を更新する運用にする。
    const string NASDAQ100_UNIVERSE_FILE = "nasdaq100-universe.txt";
    const int BREADTH_MIN_COVERAGE = 80;
    // 出来高加重のアキュムレーション/ディストリビューション判定に使う期間（IBDのA/D Rating相当）。
    const int AD_VOLUME_WINDOW = 50;
    // 銘柄間の平均ペア相関（ディスパージョン）の観測期間。
    const int CORRELATION_WINDOW = 21;
    const int CORRELATION_MIN_SYMBOLS = 20;
    // Zweig Breadth Thrust：10日騰落レシオが10営業日以内に0.40以下→0.615以上へ切り上がる。
    const int THRUST_MA_WINDOW = 10;
    const int THRUST_LOOKBACK_DAYS = 40;
    const decimal THRUST_LOWER_TRIGGER = 0.40m;
    const decimal THRUST_UPPER_TRIGGER = 0.615m;
    // 分散リスクプレミアム（VIX − 実現ボラ）に使う実現ボラの期間。VIXの30日インプライドと対応させる。
    const int REALIZED_VOL_VRP_WINDOW = 21;
    // 全指標が取得できたときの満点。採点カバレッジ率の分母として使う。
    // 配点を変更したときはここも必ず合わせる（トレンド30＋ブレッドス25＋ボラ15＋信用15＋セクター10＋需給5）。
    const decimal MARKET_RISK_TOTAL_POINTS = 100m;
    const int MAX_YAHOO_RESPONSE_BYTES = 5 * 1024 * 1024;
    const int MAX_FRED_RESPONSE_BYTES = 2 * 1024 * 1024;
    const int MAX_MARKET_DATA_AGE_CALENDAR_DAYS = 5;
    const int MAX_AUXILIARY_DATA_AGE_CALENDAR_DAYS = 7;

    static readonly TimeSpan JstOffset = TimeSpan.FromHours(9);
    static readonly Regex YahooSymbolPattern = new("^[A-Za-z0-9.^-]{1,20}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly HashSet<string> AllowedYahooRanges = new(StringComparer.Ordinal) { "2y", "1y", "6mo" };

    // ===== 過去スコアのバックフィル =====
    // 検証機能は1日1件しか観測が増えず、有意な標本が揃うまで数年かかる。
    // 取得済みの系列だけで過去の各営業日のスコアを再計算し、検証を即座に成立させる。
    //
    // 重要な制約（結果を読むときに必ず考慮すること）:
    //  1. 構成銘柄リストは現時点のスナップショットなので、過去日に当てはめると生存者バイアスが乗る。
    //     指数から外された銘柄（多くは不振銘柄）が欠け、ブレッドス系は楽観方向に歪む。
    //  2. Put/Callは過去データを取得できないため、バックフィル分は常にこの項目が欠測になる。
    //     実運用分と単純比較しないよう、エントリにSourceを持たせて区別する。
    //  3. 日次観測は重複区間を共有するため独立ではない。21営業日先を見る場合、
    //     実質的な独立標本数は「日数 ÷ 21」程度しかない。閾値の最適化に使ってはいけない。
    const string BACKFILL_RANGE = "2y";
    // 52週高値・200日線を正しく計算するため、この本数の履歴が確保できる日からのみ再計算する。
    const int BACKFILL_MIN_HISTORY_BARS = 252;
    const int BACKFILL_MAX_DAYS = 260;
    // 現在の配点体系の版。配点を変更したら必ず上げる。異なる版のスコアは同じ箱で集計しない。
    const int RUBRIC_VERSION = 2;
    // 変動理由の表示に使うのは直近数件だけなので、それ以外は採点内訳を捨ててファイルサイズを抑える。
    const int HISTORY_METRICS_RETAINED = 5;
    const int HISTORY_MAX_ENTRIES = 400;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // 通常データと遅延しても致命的ではないFREDデータでタイムアウトを分ける。
    static readonly HttpClient httpClient = CreateHttpClient(TimeSpan.FromSeconds(20));
    // FREDのfredgraph.csvは平常時でも応答が遅いことがあるため、短すぎるタイムアウトにしない。
    static readonly HttpClient fredHttpClient = CreateHttpClient(TimeSpan.FromSeconds(20));
    // 静的フィールド初期化子で例外を投げるとMainのtry/catchより先にTypeInitializationExceptionになり、
    // 原因が分からないスタックトレースだけが出る。遅延評価にしてMain内で捕捉できるようにする。
    static readonly Lazy<string> LazyOutputDirectory = new(ResolveOutputDirectory);
    static string OutputDirectory => LazyOutputDirectory.Value;

    static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 10
        };
        var c = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        // 単純な"Mozilla/5.0"だけだと逆に不自然でYahoo側にブロックされやすいため、実際のブラウザに近い文字列に強化
        c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        return c;
    }

    static DateTimeOffset JstNow() => DateTimeOffset.UtcNow.ToOffset(JstOffset);

    static string ResolveOutputDirectory()
    {
        string currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (File.Exists(Path.Combine(currentDirectory, NASDAQ100_UNIVERSE_FILE))) return currentDirectory;

        string applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (File.Exists(Path.Combine(applicationDirectory, NASDAQ100_UNIVERSE_FILE))) return applicationDirectory;

        throw new DirectoryNotFoundException("出力先を特定できません。nasdaq100-universe.txt と同じフォルダから実行してください。");
    }

    static string GetOutputPath(string fileName) => Path.Combine(OutputDirectory, fileName);

    static string ResolveContentPath(string fileName)
    {
        string currentDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        if (File.Exists(currentDirectoryPath)) return currentDirectoryPath;

        string applicationPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(applicationPath)) return applicationPath;

        throw new FileNotFoundException($"必要なコンテンツファイルが見つかりません: {fileName}");
    }

    static async Task<string> GetResponseTextWithLimitAsync(HttpClient client, string url, int maxBytes)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return await ReadResponseTextWithLimitAsync(response, maxBytes);
    }

    static async Task<string> ReadResponseTextWithLimitAsync(HttpResponseMessage response, int maxBytes)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw new InvalidDataException($"応答サイズが上限の {maxBytes:N0} バイトを超えています。");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int totalBytes = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length));
            if (read == 0) break;

            totalBytes += read;
            if (totalBytes > maxBytes)
                throw new InvalidDataException($"応答サイズが上限の {maxBytes:N0} バイトを超えています。");
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    static async Task Main()
    {
        try
        {
            Console.WriteLine("Fetching index data from Yahoo Finance...");

            // 売り抜け日/FTDは出来高が必要なため、指数ではなく実際に売買できる流動性の高いETFを使用する。
            // 判定価格には調整後終値を使い、分配金による相対リターンの歪みを避ける。
            // 過去日のスコアを再計算するため、必要な履歴の長さ（52週高値・200日線）を確保できる期間を取得する。
            var bundle = new MarketDataBundle
            {
                Sp500 = await FetchYahooDataWithRetry("SPY", BACKFILL_RANGE, requirePositiveVolume: true),
                Nasdaq = await FetchYahooDataWithRetry("QQQ", BACKFILL_RANGE, requirePositiveVolume: true)
            };
            if (bundle.Sp500[^1].Date.Date != bundle.Nasdaq[^1].Date.Date)
                throw new InvalidDataException($"SPYとQQQの基準日が一致しません（SPY: {bundle.Sp500[^1].Date:yyyy-MM-dd}, QQQ: {bundle.Nasdaq[^1].Date:yyyy-MM-dd}）。");

            // Put/Call Ratio（自前算出）。失敗しても既存のExposure機能全体を止めないよう内部で例外を握りつぶす設計
            var putCall = await FetchPutCallRatio();

            // 補助指標は取得だけ先に済ませ、計算は基準日ごとに行う（過去日の再計算に使い回すため）。
            bundle.Sector = await FetchSectorRotationData();
            bundle.Credit = await FetchCreditData();
            bundle.Volatility = await FetchVolatilityData();
            bundle.Breadth = await FetchBreadthData();

            DateTime latestDate = bundle.Sp500[^1].Date.Date;
            string marketDataAsOf = latestDate.ToString("yyyy-MM-dd");

            // 履歴はメモリ上で準備し、全指標の算出成功後に一度だけ保存する。
            // 途中失敗で当日分の最終スコアを失わないための原子性を持たせる。
            var todaySp500 = AnalyzeIndex("S&P 500（SPY）", bundle.Sp500);
            var todayNasdaq = AnalyzeIndex("Nasdaq-100（QQQ）", bundle.Nasdaq);
            int RankStatus(string status) => status switch { "Uptrend" => 2, "Pressure" => 1, _ => 0 };
            string todayCombined = RankStatus(todaySp500.StatusId) <= RankStatus(todayNasdaq.StatusId)
                ? todaySp500.StatusId : todayNasdaq.StatusId;
            var historyPreparation = PrepareHistory(todayCombined, todaySp500, todayNasdaq, putCall?.Ratio, marketDataAsOf);
            var putCallStats = historyPreparation.PutCallStats;

            var putCallOutput = putCall == null
                ? new PutCallOutput
                {
                    Status = "unavailable",
                    Underlying = PUT_CALL_UNDERLYING,
                    Note = "SPYオプションデータの取得に失敗しました。詳細はActionsのログ（PutCallRatioの行）を確認してください。"
                }
                : new PutCallOutput
                {
                    Status = "ok",
                    Underlying = putCall.Underlying,
                    CallVolume = putCall.CallVolume,
                    PutVolume = putCall.PutVolume,
                    Ratio = putCall.Ratio,
                    Sma10 = putCallStats.Sma10,
                    PercentileRank = putCallStats.PercentileRank,
                    HistoryDays = putCallStats.HistoryDays,
                    Note = "SPYの直近限月のみを集計した出来高ベースの参考指標です。0DTE・ヘッジ・満期構成の影響を受けるため、方向予想の主シグナルには使わず、極端な需給の補助確認に限定してください。"
                };

            // 0点に近いほどロング投資環境が良好、100点に近いほど市場リスクが高い。
            // 取得できない指標は0点扱いせず、利用可能な配点で正規化してカバレッジを併記する。
            var snapshot = BuildSnapshot(bundle, latestDate, putCallOutput, verbose: true)
                ?? throw new InvalidDataException("当日の市場スナップショットを算出できませんでした。");

            ApplyTodayRiskScoreSnapshot(historyPreparation.Entries, snapshot.RiskScore);

            // 過去分をまとめて再計算し、検証機能に十分な標本を与える。
            var existingMarketDates = historyPreparation.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.MarketDataAsOf))
                .Select(entry => entry.MarketDataAsOf!)
                .ToHashSet(StringComparer.Ordinal);
            var backfilled = BuildBackfillEntries(bundle, existingMarketDates);
            Console.WriteLine($"Backfilled {backfilled.Count} past sessions.");

            var allEntries = historyPreparation.Entries.Concat(backfilled)
                .OrderBy(entry => entry.Date, StringComparer.Ordinal).ToList();
            UpdateScoreValidationOutcomes(allEntries, bundle.Sp500, bundle.Nasdaq);
            var marketRiskChange = BuildMarketRiskChange(allEntries);
            var scoreValidation = BuildScoreValidation(allEntries);
            PersistHistory(allEntries);
            Console.WriteLine("history.json has been updated.");

            var output = new
            {
                lastUpdated = JstNow().ToString("yyyy-MM-dd HH:mm:ss"),
                marketDataAsOf,
                combinedStatus = snapshot.CombinedStatus,
                combinedDrivenBy = snapshot.CombinedDrivenBy,
                sp500 = snapshot.Sp500,
                nasdaq = snapshot.Nasdaq,
                putCallRatio = putCallOutput,
                sectorRotation = snapshot.Sector,
                creditRiskAppetite = snapshot.Credit,
                volatilityRegime = snapshot.Volatility,
                marketBreadth = snapshot.Breadth,
                marketRiskScore = snapshot.RiskScore,
                marketRiskChange,
                scoreValidation
            };

            var json = JsonSerializer.Serialize(output, JsonOptions);
            WriteTextAtomically(GetOutputPath("data.json"), json);
            Console.WriteLine("data.json has been generated successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error occurred: {ex.Message}");
            Environment.Exit(1); // GitHub Actions側に失敗を検知させる
        }
    }

    static void WriteTextAtomically(string path, string content)
    {
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // ================= データ取得 =================

    static async Task<List<DailyData>> FetchYahooDataWithRetry(string symbol, string range, bool requirePositiveVolume = false, int maxAttempts = 3)
    {
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await FetchYahooData(symbol, range, requirePositiveVolume);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Console.WriteLine($"[{symbol}] attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }
        throw new Exception($"Failed to fetch {symbol} after {maxAttempts} attempts: {lastEx?.Message}");
    }

    static async Task<List<DailyData>> FetchYahooData(string symbol, string range, bool requirePositiveVolume)
    {
        if (!YahooSymbolPattern.IsMatch(symbol))
            throw new ArgumentException($"許可されないYahooシンボルです: {symbol}", nameof(symbol));
        if (!AllowedYahooRanges.Contains(range))
            throw new ArgumentException($"許可されないYahoo取得期間です: {range}", nameof(range));

        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval=1d&range={Uri.EscapeDataString(range)}";

        var responseString = await GetResponseTextWithLimitAsync(httpClient, url, MAX_YAHOO_RESPONSE_BYTES);
        var yahooData = JsonSerializer.Deserialize<YahooResult>(responseString);

        var result = yahooData?.Chart?.Result?.FirstOrDefault();
        if (result?.Timestamp == null || result.Indicators?.Quote?.FirstOrDefault() == null)
        {
            throw new Exception($"Yahoo Financeからのデータ構造が無効です ({symbol})。");
        }

        var timestamps = result.Timestamp;
        var quote = result.Indicators.Quote.First();
        // close/volume配列自体がnullで返ってくる異常応答に備える（NullReferenceException対策）
        var closes = quote.Close ?? throw new Exception($"close配列が取得できませんでした ({symbol})。");
        var volumes = quote.Volume ?? throw new Exception($"volume配列が取得できませんでした ({symbol})。");

        var adjustedCloses = result.Indicators.AdjClose?.FirstOrDefault()?.AdjustedClose;
        var dailyData = new List<DailyData>();
        for (int i = 0; i < timestamps.Length; i++)
        {
            if (i < closes.Length && i < volumes.Length && closes[i].HasValue && volumes[i].HasValue)
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime.Date;
                // ETFでは分配金・権利落ちによるリターンの歪みを避けるため調整後終値を優先する。
                // 指数などadjusted closeがない銘柄は通常終値へ安全にフォールバックする。
                decimal adjustedClose = i < (adjustedCloses?.Length ?? 0) && adjustedCloses![i].HasValue
                    ? adjustedCloses[i]!.Value
                    : closes[i]!.Value;
                if (adjustedClose <= 0m)
                    throw new InvalidDataException($"0以下の終値を受信しました ({symbol}, {date:yyyy-MM-dd})。");
                if (volumes[i]!.Value < 0)
                    throw new InvalidDataException($"負の出来高を受信しました ({symbol}, {date:yyyy-MM-dd})。");
                dailyData.Add(new DailyData(date, adjustedClose, volumes[i]!.Value));
            }
        }

        var ordered = dailyData.OrderBy(x => x.Date).ToList();
        if (ordered.Count < 100)
        {
            throw new Exception($"計算に必要な100日分のデータが不足しています ({symbol})。");
        }
        if (ordered.Select(d => d.Date).Distinct().Count() != ordered.Count)
            throw new InvalidDataException($"重複した取引日を受信しました ({symbol})。");
        if (ordered[^1].Date > DateTime.UtcNow.Date.AddDays(1))
            throw new InvalidDataException($"未来日付の市場データを受信しました ({symbol})。");
        if ((DateTime.UtcNow.Date - ordered[^1].Date).TotalDays > MAX_MARKET_DATA_AGE_CALENDAR_DAYS)
            throw new InvalidDataException($"市場データが{MAX_MARKET_DATA_AGE_CALENDAR_DAYS}日超古いため更新を中止しました ({symbol}: {ordered[^1].Date:yyyy-MM-dd})。");
        if (requirePositiveVolume && ordered.Any(d => d.Volume <= 0))
            throw new InvalidDataException($"出来高が0以下の日を受信したため、出来高ベース判定を中止しました ({symbol})。");
        return ordered;
    }

    // 調整後終値を必要とするため、chartエンドポイントを最大5並列で取得する。
    static async Task<Dictionary<string, List<DailyData>>> FetchYahooBreadthData(IEnumerable<string> symbols)
    {
        using var gate = new SemaphoreSlim(5);
        var uniqueSymbols = symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tasks = uniqueSymbols.Select(async symbol =>
        {
            await gate.WaitAsync();
            try
            {
                // 過去日のブレッドスを再計算するため、指数と同じ期間を取得する。
                // ここだけ短いと必要履歴（52週高値・200日線）を満たせず全銘柄が脱落する。
                var data = await FetchYahooDataWithRetry(symbol, BACKFILL_RANGE);
                return (Symbol: symbol, Data: data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Breadth] {symbol} skipped: {ex.Message}");
                return (Symbol: symbol, Data: (List<DailyData>?)null);
            }
            finally
            {
                gate.Release();
            }
        });

        var fetched = await Task.WhenAll(tasks);
        return fetched
            .Where(x => x.Data != null && x.Data.Count >= 100)
            .ToDictionary(x => x.Symbol, x => x.Data!, StringComparer.OrdinalIgnoreCase);
    }

    // ================= Put/Call Ratio（自前算出） =================

    static string? _yahooCrumb = null;

    static async Task<string?> GetYahooCrumb()
    {
        if (_yahooCrumb != null) return _yahooCrumb;
        try
        {
            // 1. まずCookieを発行してもらう（同じhttpClientを使い続けることで、.NETのHttpClientHandlerが
            //    標準で持つCookieContainerにより、以降のリクエストにこのCookieが自動的に付与される）
            using (var cookieResponse = await httpClient.GetAsync("https://fc.yahoo.com"))
            {
                Console.WriteLine($"[PutCallRatio] Cookie取得ステータス: {(int)cookieResponse.StatusCode}");
            }

            // 2. 発行されたCookieを使ってcrumb（認証トークン）を取得
            using var crumbResponse = await httpClient.GetAsync("https://query2.finance.yahoo.com/v1/test/getcrumb");

            if (!crumbResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[PutCallRatio] crumb取得に失敗しました（status={(int)crumbResponse.StatusCode}）。");
                return null;
            }
            var crumb = await ReadResponseTextWithLimitAsync(crumbResponse, 4096);
            if (string.IsNullOrWhiteSpace(crumb) || crumb.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[PutCallRatio] crumbの内容が無効です。");
                return null;
            }

            _yahooCrumb = crumb.Trim();
            Console.WriteLine("[PutCallRatio] crumbの取得に成功しました。");
            return _yahooCrumb;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PutCallRatio] crumb取得中に例外: {ex.Message}");
            return null;
        }
    }

    static async Task<PutCallResult?> FetchPutCallRatio()
    {
        // 設計方針：
        // ・呼び出し回数を絞る（複数限月を合算する方式より壊れにくさを優先）
        // ・SPYは週次/デイリー(0DTE)オプションの出来高が非常に大きいため、
        //   直近限月だけでもその日の出来高のかなりの部分を捉えられる実用上の近似として採用
        // ・失敗しても例外を外に投げず null を返す＝Stock Market Exposure機能を道連れにしない
        // ・Yahoo Finance側が近年Cookie/crumb認証を要求するようになっているため、先にcrumbを取得してから使う
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var crumb = await GetYahooCrumb();
                if (crumb == null)
                {
                    Console.WriteLine($"[PutCallRatio] attempt {attempt}/3: crumbが取得できませんでした。");
                    if (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(3 * attempt)); continue; }
                    return null;
                }

                var url = $"https://query1.finance.yahoo.com/v7/finance/options/{PUT_CALL_UNDERLYING}?crumb={Uri.EscapeDataString(crumb)}";
                using var response = await httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[PutCallRatio] attempt {attempt}/3: HTTPステータス {(int)response.StatusCode} が返されました。");
                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        // crumbが無効化された可能性があるため、キャッシュを破棄して次のリトライで取り直す
                        Console.WriteLine("[PutCallRatio] 認証エラーの可能性があるため、crumbキャッシュを破棄します。");
                        _yahooCrumb = null;
                    }
                    if (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(3 * attempt)); continue; }
                    return null;
                }

                var responseString = await ReadResponseTextWithLimitAsync(response, MAX_YAHOO_RESPONSE_BYTES);
                var parsed = JsonSerializer.Deserialize<YahooOptionsResult>(responseString);
                var result = parsed?.OptionChain?.Result?.FirstOrDefault();
                var group = result?.Options?.FirstOrDefault();

                if (group == null)
                {
                    Console.WriteLine("[PutCallRatio] オプションチェーンのデータ構造が無効です。");
                    return null;
                }

                long callVolume = (group.Calls ?? Array.Empty<OptionContract>()).Sum(c => c.Volume ?? 0);
                long putVolume = (group.Puts ?? Array.Empty<OptionContract>()).Sum(p => p.Volume ?? 0);

                if (callVolume <= 0)
                {
                    Console.WriteLine("[PutCallRatio] コール出来高が0のため算出をスキップします。");
                    return null;
                }

                return new PutCallResult
                {
                    Underlying = PUT_CALL_UNDERLYING,
                    CallVolume = callVolume,
                    PutVolume = putVolume,
                    Ratio = Math.Round((decimal)putVolume / callVolume, 3)
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PutCallRatio] attempt {attempt}/3 failed (non-fatal): {ex.Message}");
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }
        Console.WriteLine("[PutCallRatio] 3回試行しましたが取得できませんでした。putCallRatioは省略されます。");
        return null;
    }

    // ================= セクターローテーション（自前算出） =================

    // 取得と計算を分離する。過去日のスコアを再計算するために、同じ生データへ何度も
    // 別の基準日を当てて計算できるようにしておく必要がある。
    static async Task<SectorRotationData?> FetchSectorRotationData()
    {
        try
        {
            var sectors = new Dictionary<string, List<DailyData>>(StringComparer.OrdinalIgnoreCase);
            var proxies = new Dictionary<string, List<DailyData>>(StringComparer.OrdinalIgnoreCase);

            async Task FetchInto(Dictionary<string, List<DailyData>> target, string symbol, string label)
            {
                try
                {
                    target[symbol] = await FetchYahooDataWithRetry(symbol, BACKFILL_RANGE);
                }
                catch (Exception ex)
                {
                    // 1銘柄の失敗は他の銘柄の表示を止める理由にしない
                    Console.WriteLine($"[SectorRotation] {symbol} の取得に失敗（この銘柄のみスキップ）: {ex.Message}");
                }
                // Yahoo側への負荷を抑えるため、連続リクエストの間に軽くウェイトを入れる
                await Task.Delay(300);
            }

            foreach (var (symbol, name) in SECTOR_ETFS) await FetchInto(sectors, symbol, name);
            // 値幅代理指標（RSP: 均等加重、IWM: 小型株）。500銘柄の個別スキャンをせずに
            // 「上昇が広いか一部の大型株に偏っているか」を近似する。
            foreach (var (symbol, name) in BREADTH_PROXY_ETFS) await FetchInto(proxies, symbol, name);

            if (sectors.Count == 0)
            {
                Console.WriteLine("[SectorRotation] 全セクターの取得に失敗しました。");
                return null;
            }

            return new SectorRotationData
            {
                SpyData = await FetchYahooDataWithRetry("SPY", BACKFILL_RANGE),
                Sectors = sectors,
                Proxies = proxies
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SectorRotation] fetch failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    static SectorRotationResult? ComputeSectorRotation(SectorRotationData source, DateTime asOf)
    {
        var spyData = TruncateTo(source.SpyData, asOf);
        if (spyData == null) return null;

        decimal spyReturn1m = ComputeReturnPct(spyData, SECTOR_RETURN_1M_DAYS);
        decimal spyReturn3m = ComputeReturnPct(spyData, SECTOR_RETURN_3M_DAYS);

        SectorInfo? Build(string symbol, string name, List<DailyData> raw)
        {
            var data = TruncateTo(raw, asOf);
            if (data == null || data.Count <= SECTOR_RETURN_3M_DAYS) return null;
            decimal r1m = ComputeReturnPct(data, SECTOR_RETURN_1M_DAYS);
            decimal r3m = ComputeReturnPct(data, SECTOR_RETURN_3M_DAYS);
            return new SectorInfo
            {
                Symbol = symbol,
                Name = name,
                Return1m = r1m,
                Return3m = r3m,
                RelStrength1m = Math.Round(r1m - spyReturn1m, 2),
                RelStrength3m = Math.Round(r3m - spyReturn3m, 2)
            };
        }

        var sectorList = new List<SectorInfo>();
        foreach (var (symbol, name) in SECTOR_ETFS)
        {
            if (!source.Sectors.TryGetValue(symbol, out var raw)) continue;
            var info = Build(symbol, name, raw);
            if (info != null) sectorList.Add(info);
        }
        if (sectorList.Count == 0) return null;

        var breadthList = new List<SectorInfo>();
        foreach (var (symbol, name) in BREADTH_PROXY_ETFS)
        {
            if (!source.Proxies.TryGetValue(symbol, out var raw)) continue;
            var info = Build(symbol, name, raw);
            if (info != null) breadthList.Add(info);
        }

        // ディフェンシブ（生活必需品・公益・ヘルスケア）とシクリカル（一般消費財・テクノロジー・資本財）の
        // 平均リターン差。プラス幅が大きいほど資金が守りに回っている＝リスクオフの進行。
        decimal? GroupAverage(string[] symbols, Func<SectorInfo, decimal> selector)
        {
            var values = sectorList
                .Where(sector => symbols.Contains(sector.Symbol, StringComparer.OrdinalIgnoreCase))
                .Select(selector).ToList();
            return values.Count == 0 ? null : Math.Round(values.Average(), 2);
        }

        decimal? defensive1m = GroupAverage(DEFENSIVE_SECTORS, s => s.Return1m);
        decimal? cyclical1m = GroupAverage(CYCLICAL_SECTORS, s => s.Return1m);
        decimal? defensive3m = GroupAverage(DEFENSIVE_SECTORS, s => s.Return3m);
        decimal? cyclical3m = GroupAverage(CYCLICAL_SECTORS, s => s.Return3m);

        return new SectorRotationResult
        {
            SpyReturn1m = spyReturn1m,
            SpyReturn3m = spyReturn3m,
            DefensiveReturn1m = defensive1m,
            CyclicalReturn1m = cyclical1m,
            RotationSpread1m = defensive1m.HasValue && cyclical1m.HasValue
                ? Math.Round(defensive1m.Value - cyclical1m.Value, 2) : null,
            RotationSpread3m = defensive3m.HasValue && cyclical3m.HasValue
                ? Math.Round(defensive3m.Value - cyclical3m.Value, 2) : null,
            // 3ヶ月の相対強度が高い順（＝リーダーシップが強い順）に並べる
            Sectors = sectorList.OrderByDescending(s => s.RelStrength3m).ToList(),
            BreadthProxies = breadthList
        };
    }

    // 指定した基準日までの系列を切り出す。基準日に取引が無い銘柄はnullを返して集計から外す。
    // （鮮度チェックが5営業日まで許容するため、日付をそろえないと別々の日の合成になる）
    static List<DailyData>? TruncateTo(List<DailyData> data, DateTime asOf)
    {
        int index = data.FindLastIndex(day => day.Date.Date <= asOf.Date);
        if (index < 0 || data[index].Date.Date != asOf.Date) return null;
        return index == data.Count - 1 ? data : data.GetRange(0, index + 1);
    }

    static decimal ComputeReturnPct(List<DailyData> data, int lookbackDays)
    {
        int n = data.Count;
        int startIdx = Math.Max(0, n - 1 - lookbackDays);
        decimal startClose = data[startIdx].AdjustedClose;
        decimal endClose = data[n - 1].AdjustedClose;
        return Math.Round((endClose - startClose) / startClose * 100m, 2);
    }

    // ================= Credit Risk Appetite（自前算出） =================

    static async Task<CreditData?> FetchCreditData()
    {
        // HYGとLQDの「差」自体が指標の本体なので、片方だけ成功しても意味がない。
        // そのためセクターローテーションのような1銘柄ずつの部分成功は許容せず、
        // どちらかが失敗したら全体をunavailable扱いにする
        try
        {
            var hygData = await FetchYahooDataWithRetry(CREDIT_RISK_ON_SYMBOL, BACKFILL_RANGE);
            await Task.Delay(300); // Yahoo側への負荷を抑える
            var lqdData = await FetchYahooDataWithRetry(CREDIT_RISK_OFF_SYMBOL, BACKFILL_RANGE);

            // FREDが一時的に取得不能でも、HYG/LQDの比較は表示を継続する。
            List<(DateTime Date, decimal Value)> hyOas = new();
            try { hyOas = await FetchHyOasSeries(); }
            catch (Exception ex) { Console.WriteLine($"[CreditRiskAppetite] HY OAS fetch failed (non-fatal): {ex.Message}"); }

            return new CreditData { HygData = hygData, LqdData = lqdData, HyOasSeries = hyOas };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreditRiskAppetite] fetch failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    static CreditRiskAppetiteResult? ComputeCreditRiskAppetite(CreditData source, DateTime asOf)
    {
        var hygData = TruncateTo(source.HygData, asOf);
        var lqdData = TruncateTo(source.LqdData, asOf);
        if (hygData == null || lqdData == null) return null;
        if (hygData.Count <= SECTOR_RETURN_3M_DAYS || lqdData.Count <= SECTOR_RETURN_3M_DAYS) return null;

        decimal hyg1m = ComputeReturnPct(hygData, SECTOR_RETURN_1M_DAYS);
        decimal hyg3m = ComputeReturnPct(hygData, SECTOR_RETURN_3M_DAYS);
        decimal lqd1m = ComputeReturnPct(lqdData, SECTOR_RETURN_1M_DAYS);
        decimal lqd3m = ComputeReturnPct(lqdData, SECTOR_RETURN_3M_DAYS);
        var hyOas = HyOasAsOf(source.HyOasSeries, asOf);

        return new CreditRiskAppetiteResult
        {
            HygReturn1m = hyg1m,
            HygReturn3m = hyg3m,
            LqdReturn1m = lqd1m,
            LqdReturn3m = lqd3m,
            Spread1m = Math.Round(hyg1m - lqd1m, 2),
            Spread3m = Math.Round(hyg3m - lqd3m, 2),
            HyOasPct = hyOas?.ValuePct,
            HyOasChange1mBps = hyOas?.Change1mBps,
            HyOasDate = hyOas?.Date
        };
    }

    // 指定基準日時点で判明していた最新のHY OASを返す。過去日の再計算で未来の値を使わないための処理。
    static HyOasResult? HyOasAsOf(List<(DateTime Date, decimal Value)> ordered, DateTime asOf)
    {
        if (ordered.Count == 0) return null;
        int index = ordered.FindLastIndex(item => item.Date.Date <= asOf.Date);
        if (index < 0) return null;

        var latest = ordered[index];
        // 基準日から見て古すぎる値は、その時点で「未取得」だったものとして扱う。
        if ((asOf.Date - latest.Date.Date).TotalDays > MAX_AUXILIARY_DATA_AGE_CALENDAR_DAYS)
        {
            if (asOf.Date == DateTime.UtcNow.Date || index == ordered.Count - 1)
                Console.WriteLine($"[CreditRiskAppetite] HY OASが{MAX_AUXILIARY_DATA_AGE_CALENDAR_DAYS}日超古いため採用しません ({latest.Date:yyyy-MM-dd})。");
            return null;
        }

        int lookbackIndex = Math.Max(0, index - SECTOR_RETURN_1M_DAYS);
        decimal change1mBps = Math.Round((latest.Value - ordered[lookbackIndex].Value) * 100m, 0);
        return new HyOasResult
        {
            ValuePct = latest.Value,
            Change1mBps = change1mBps,
            Date = latest.Date.ToString("yyyy-MM-dd")
        };
    }

    static async Task<List<(DateTime Date, decimal Value)>> FetchHyOasSeries()
    {
        string url = $"https://fred.stlouisfed.org/graph/fredgraph.csv?id={HY_OAS_FRED_SERIES}";

        // FREDは一時的に応答が非常に遅くなることがある。1回のタイムアウトで恒久的に「未取得」にしない。
        string? csv = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                csv = await GetResponseTextWithLimitAsync(fredHttpClient, url, MAX_FRED_RESPONSE_BYTES);
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CreditRiskAppetite] FRED attempt {attempt}/3 failed: {ex.Message}");
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
        var observations = new List<(DateTime Date, decimal Value)>();
        if (csv == null) return observations;

        foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var fields = line.Trim().Split(',', 2);
            if (fields.Length != 2 || fields[1].Trim() == ".") continue;
            if (DateTime.TryParseExact(fields[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                decimal.TryParse(fields[1].Trim().Trim('"'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                observations.Add((date, value));
            }
        }

        return observations.OrderBy(x => x.Date).ToList();
    }

    // ================= ボラティリティ警戒灯 =================

    static async Task<VolatilityData?> FetchVolatilityData()
    {
        try
        {
            var vixData = await FetchYahooDataWithRetry(VIX_SYMBOL, BACKFILL_RANGE);

            // ^VIX3Mの配信停止でボラティリティ判定そのものを失わないよう、失敗を致命的に扱わない。
            List<DailyData>? vix3mData = null;
            try
            {
                await Task.Delay(250);
                vix3mData = await FetchYahooDataWithRetry(VIX3M_SYMBOL, BACKFILL_RANGE, maxAttempts: 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Volatility] {VIX3M_SYMBOL} を利用できません。実現ボラティリティで代替します: {ex.Message}");
            }

            return new VolatilityData { VixData = vixData, Vix3mData = vix3mData };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Volatility] fetch failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    static VolatilityResult? ComputeVolatilityRegime(VolatilityData source, List<DailyData> spyData, DateTime asOf)
    {
        var vixData = TruncateTo(source.VixData, asOf);
        if (vixData == null || vixData.Count < VIX_SMA_WINDOW) return null;

        decimal vix = vixData[^1].AdjustedClose;
        decimal vixSma20 = Math.Round(vixData.TakeLast(VIX_SMA_WINDOW).Average(d => d.AdjustedClose), 2);

        // 分散リスクプレミアム = インプライド(VIX) − 実現ボラ。
        // VIXの水準そのものより、「現実の変動に対してオプションが割安か」を見る。
        // マイナス（実現>インプライド）は市場が現実の変動に追いつけていない慢心のサイン。
        decimal? realizedVol21 = RealizedVolatilityPct(spyData, REALIZED_VOL_VRP_WINDOW);
        decimal? varianceRiskPremium = realizedVol21.HasValue ? Math.Round(vix - realizedVol21.Value, 2) : null;

        var vix3mData = source.Vix3mData == null ? null : TruncateTo(source.Vix3mData, asOf);
        if (vix3mData is { Count: > 0 })
        {
            decimal vix3m = vix3mData[^1].AdjustedClose;
            decimal termSlopePct = Math.Round((vix - vix3m) / vix3m * 100m, 2);
            return new VolatilityResult
            {
                Vix = vix,
                VixSma20 = vixSma20,
                RealizedVol21Pct = realizedVol21,
                VarianceRiskPremium = varianceRiskPremium,
                Vix3m = vix3m,
                TermSlopePct = termSlopePct,
                TermStructure = termSlopePct > 0 ? "Backwardation" : "Contango",
                TermSource = "VIX3M"
            };
        }

        decimal? shortVol = RealizedVolatilityPct(spyData, REALIZED_VOL_SHORT_WINDOW);
        decimal? longVol = RealizedVolatilityPct(spyData, REALIZED_VOL_LONG_WINDOW);
        if (!shortVol.HasValue || !longVol.HasValue || longVol.Value <= 0m) return null;

        decimal fallbackSlopePct = Math.Round((shortVol.Value - longVol.Value) / longVol.Value * 100m, 2);
        return new VolatilityResult
        {
            Vix = vix,
            VixSma20 = vixSma20,
            RealizedVol21Pct = realizedVol21,
            VarianceRiskPremium = varianceRiskPremium,
            Vix3m = null,
            RealizedVolShortPct = shortVol,
            RealizedVolLongPct = longVol,
            TermSlopePct = fallbackSlopePct,
            TermStructure = fallbackSlopePct > 0 ? "Backwardation" : "Contango",
            TermSource = "RealizedVol"
        };
    }

    // 対数リターンの標本標準偏差を年率換算した実現ボラティリティ(%)。
    static decimal? RealizedVolatilityPct(List<DailyData> data, int window)
    {
        if (window < 2 || data.Count < window + 1) return null;

        var logReturns = new List<double>(window);
        for (int i = data.Count - window; i < data.Count; i++)
        {
            double previous = (double)data[i - 1].AdjustedClose;
            double current = (double)data[i].AdjustedClose;
            if (previous <= 0d || current <= 0d) return null;
            logReturns.Add(Math.Log(current / previous));
        }

        double mean = logReturns.Average();
        double variance = logReturns.Sum(value => (value - mean) * (value - mean)) / (logReturns.Count - 1);
        double annualized = Math.Sqrt(variance) * Math.Sqrt(TRADING_DAYS_PER_YEAR) * 100d;
        return double.IsFinite(annualized) ? Math.Round((decimal)annualized, 2) : null;
    }

    // ================= Nasdaq-100 真の市場ブレッドス =================

    static async Task<BreadthData?> FetchBreadthData()
    {
        try
        {
            string universePath = ResolveContentPath(NASDAQ100_UNIVERSE_FILE);

            var symbols = File.ReadLines(universePath)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (symbols.Count == 0) return null;

            return new BreadthData
            {
                ExpectedConstituents = symbols.Count,
                Symbols = await FetchYahooBreadthData(symbols)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Breadth] fetch failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    static MarketBreadthResult? ComputeBreadth(BreadthData source, DateTime referenceDate, bool verbose)
    {
        try
        {
            // 基準日に取引が成立している銘柄だけを使う。
            // 鮮度チェックは5営業日まで許容するため、売買停止・上場廃止などで数日前の終値のまま返る銘柄が混ざる。
            // それを「今日の上昇/下落」として数えるとA/Dラインと騰落数が別々の日の合成になってしまう。
            var analyzed = new List<List<DailyData>>();
            foreach (var raw in source.Symbols.Values)
            {
                var data = TruncateTo(raw, referenceDate);
                // 52週高値・200日線を正しく求めるため、十分な履歴がある銘柄のみ採用する。
                if (data != null && data.Count >= BACKFILL_MIN_HISTORY_BARS) analyzed.Add(data);
            }

            int staleSymbols = source.Symbols.Count - analyzed.Count;
            if (verbose && staleSymbols > 0)
                Console.WriteLine($"[Breadth] {staleSymbols} symbols skipped: 基準日({referenceDate:yyyy-MM-dd})に取引が無い、または履歴不足。");
            if (analyzed.Count < BREADTH_MIN_COVERAGE)
            {
                if (verbose) Console.WriteLine($"[Breadth] insufficient coverage: {analyzed.Count}/{source.ExpectedConstituents}");
                return null;
            }

            int symbolsCount = source.ExpectedConstituents;
            int above50 = 0, above50Eligible = 0;
            int above200 = 0, above200Eligible = 0;
            int newHighs = 0, newLows = 0;
            int advances = 0, declines = 0;
            var adNetByDate = new Dictionary<DateTime, int>();

            foreach (var data in analyzed)
            {
                decimal latest = data[^1].AdjustedClose;
                if (data.Count >= 50)
                {
                    above50Eligible++;
                    if (latest >= data.TakeLast(50).Average(d => d.AdjustedClose)) above50++;
                }
                if (data.Count >= 200)
                {
                    above200Eligible++;
                    if (latest >= data.TakeLast(200).Average(d => d.AdjustedClose)) above200++;
                }

                var trailingYear = data.TakeLast(Math.Min(252, data.Count)).Select(d => d.AdjustedClose).ToList();
                if (latest >= trailingYear.Max()) newHighs++;
                if (latest <= trailingYear.Min()) newLows++;

                // 使うのは直近20営業日分の騰落だけなので、1年分すべてを走査しない。
                int adStart = Math.Max(1, data.Count - 20);
                for (int i = adStart; i < data.Count; i++)
                {
                    int net = data[i].AdjustedClose > data[i - 1].AdjustedClose ? 1 :
                              data[i].AdjustedClose < data[i - 1].AdjustedClose ? -1 : 0;
                    DateTime date = data[i].Date.Date;
                    adNetByDate[date] = adNetByDate.TryGetValue(date, out int current) ? current + net : net;
                }

                if (latest > data[^2].AdjustedClose) advances++;
                else if (latest < data[^2].AdjustedClose) declines++;
            }

            var latestAdDates = adNetByDate.Keys.OrderBy(d => d).TakeLast(20).ToList();
            int adLineChange20d = latestAdDates.Sum(date => adNetByDate[date]);

            // ---- 出来高加重のアキュムレーション/ディストリビューション（IBDのA/D Rating相当） ----
            // 上昇日の出来高と下落日の出来高を比べ、機関が拾っているか降りているかを銘柄ごとに判定する。
            int accumulationCount = 0, adRatioEligible = 0, stealthDistributionCount = 0, aboveSma50ForStealth = 0;
            foreach (var data in analyzed)
            {
                if (data.Count < AD_VOLUME_WINDOW + 1) continue;

                decimal upVolume = 0m, downVolume = 0m;
                for (int i = data.Count - AD_VOLUME_WINDOW; i < data.Count; i++)
                {
                    if (data[i].AdjustedClose > data[i - 1].AdjustedClose) upVolume += data[i].Volume;
                    else if (data[i].AdjustedClose < data[i - 1].AdjustedClose) downVolume += data[i].Volume;
                }

                adRatioEligible++;
                bool underAccumulation = downVolume <= 0m ? upVolume > 0m : upVolume / downVolume >= 1m;
                if (underAccumulation) accumulationCount++;

                // ステルス配分：価格は50日線の上なのに、出来高は下落日に偏っている＝最も気づきにくい売り抜け。
                decimal sma50 = data.TakeLast(50).Average(d => d.AdjustedClose);
                if (data[^1].AdjustedClose >= sma50)
                {
                    aboveSma50ForStealth++;
                    if (!underAccumulation) stealthDistributionCount++;
                }
            }

            // ---- 10日騰落レシオ と Zweig Breadth Thrust ----
            var advanceDeclineByDate = new SortedDictionary<DateTime, (int Advances, int Declines)>();
            foreach (var data in analyzed)
            {
                int thrustStart = Math.Max(1, data.Count - THRUST_LOOKBACK_DAYS);
                for (int i = thrustStart; i < data.Count; i++)
                {
                    DateTime date = data[i].Date.Date;
                    advanceDeclineByDate.TryGetValue(date, out var counts);
                    if (data[i].AdjustedClose > data[i - 1].AdjustedClose) counts.Advances++;
                    else if (data[i].AdjustedClose < data[i - 1].AdjustedClose) counts.Declines++;
                    advanceDeclineByDate[date] = counts;
                }
            }

            var advanceRatios = advanceDeclineByDate.Values
                .Where(counts => counts.Advances + counts.Declines > 0)
                .Select(counts => (decimal)counts.Advances / (counts.Advances + counts.Declines))
                .ToList();

            decimal? advanceRatioSma10 = null;
            bool breadthThrustDetected = false;
            if (advanceRatios.Count >= THRUST_MA_WINDOW)
            {
                var movingAverages = new List<decimal>();
                for (int i = THRUST_MA_WINDOW - 1; i < advanceRatios.Count; i++)
                    movingAverages.Add(advanceRatios.Skip(i - THRUST_MA_WINDOW + 1).Take(THRUST_MA_WINDOW).Average());

                advanceRatioSma10 = Math.Round(movingAverages[^1], 3);

                // 直近10営業日以内に「0.40以下 → 0.615以上」を10営業日以内で達成していれば点灯。
                for (int j = Math.Max(0, movingAverages.Count - THRUST_MA_WINDOW); j < movingAverages.Count && !breadthThrustDetected; j++)
                {
                    if (movingAverages[j] < THRUST_UPPER_TRIGGER) continue;
                    for (int i = Math.Max(0, j - THRUST_MA_WINDOW); i < j; i++)
                    {
                        if (movingAverages[i] <= THRUST_LOWER_TRIGGER) { breadthThrustDetected = true; break; }
                    }
                }
            }

            return new MarketBreadthResult
            {
                AvgPairwiseCorrelation = AveragePairwiseCorrelation(analyzed, CORRELATION_WINDOW),
                AccumulationPct = adRatioEligible == 0 ? null : Math.Round((decimal)accumulationCount / adRatioEligible * 100m, 1),
                StealthDistributionPct = aboveSma50ForStealth == 0 ? null : Math.Round((decimal)stealthDistributionCount / aboveSma50ForStealth * 100m, 1),
                AdvanceRatioSma10 = advanceRatioSma10,
                BreadthThrustDetected = breadthThrustDetected,
                UniverseAsOf = "2026-05-01",
                ExpectedConstituents = symbolsCount,
                AnalyzedConstituents = analyzed.Count,
                CoveragePct = Math.Round((decimal)analyzed.Count / symbolsCount * 100m, 1),
                AboveSma50Pct = above50Eligible == 0 ? null : Math.Round((decimal)above50 / above50Eligible * 100m, 1),
                AboveSma200Pct = above200Eligible == 0 ? null : Math.Round((decimal)above200 / above200Eligible * 100m, 1),
                NewHighs52Week = newHighs,
                NewLows52Week = newLows,
                Advances = advances,
                Declines = declines,
                AdvanceDeclineNet = advances - declines,
                AdLineChange20d = adLineChange20d
            };
        }
        catch (Exception ex)
        {
            if (verbose) Console.WriteLine($"[Breadth] compute failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    // 構成銘柄どうしの平均ペア相関（ディスパージョンの逆数的な指標）。
    // 高いほど「個別材料が効かないマクロ一括相場」で、分散もセクター選択も効きにくい。
    // 全ペアを直接計算せず、標準化リターンの合計の分散から導く：
    //   ρ̄ = [ (1/T)Σ_t (Σ_i z_it)^2 − n ] / (n(n−1))
    static decimal? AveragePairwiseCorrelation(List<List<DailyData>> series, int window)
    {
        // 位置（末尾からn本目）でそろえると、Yahooが欠測日を落としている銘柄でリターンが日付ずれを起こす。
        // 通常のNDX構成銘柄はカレンダーが一致するため結果は変わらないが、売買停止などで
        // 歯抜けが出た銘柄が混ざったときに相関が不当に低く出るのを防ぐ保険として日付でそろえる。
        var dateCounts = new Dictionary<DateTime, int>();
        foreach (var data in series)
        {
            foreach (var day in data.TakeLast(window * 3))
            {
                DateTime date = day.Date.Date;
                dateCounts[date] = dateCounts.TryGetValue(date, out int seen) ? seen + 1 : 1;
            }
        }

        // 大半の銘柄が取引している日だけを共通カレンダーとして採用する。
        int requiredSymbols = (int)Math.Ceiling(series.Count * 0.9);
        var targetDates = dateCounts
            .Where(entry => entry.Value >= requiredSymbols)
            .Select(entry => entry.Key)
            .OrderBy(date => date)
            .TakeLast(window + 1)
            .ToList();
        if (targetDates.Count < window + 1) return null;

        var standardized = new List<double[]>();
        foreach (var data in series)
        {
            var closesByDate = new Dictionary<DateTime, decimal>();
            foreach (var day in data.TakeLast(window * 3)) closesByDate[day.Date.Date] = day.AdjustedClose;

            var returns = new double[window];
            bool usable = true;
            for (int k = 0; k < window; k++)
            {
                if (!closesByDate.TryGetValue(targetDates[k], out decimal previousClose) ||
                    !closesByDate.TryGetValue(targetDates[k + 1], out decimal currentClose) ||
                    previousClose <= 0m || currentClose <= 0m)
                {
                    usable = false;
                    break;
                }
                returns[k] = Math.Log((double)currentClose / (double)previousClose);
            }
            if (!usable) continue;

            double mean = returns.Average();
            // 上式が厳密に成り立つよう、母分散（Tで割る）でそろえる。
            double variance = returns.Sum(value => (value - mean) * (value - mean)) / window;
            if (variance <= 0d) continue;

            double deviation = Math.Sqrt(variance);
            for (int k = 0; k < window; k++) returns[k] = (returns[k] - mean) / deviation;
            standardized.Add(returns);
        }

        int count = standardized.Count;
        if (count < CORRELATION_MIN_SYMBOLS) return null;

        double sumOfSquares = 0d;
        for (int t = 0; t < window; t++)
        {
            double total = 0d;
            foreach (var z in standardized) total += z[t];
            sumOfSquares += total * total;
        }

        double correlation = (sumOfSquares / window - count) / ((double)count * (count - 1));
        return double.IsFinite(correlation)
            ? Math.Round((decimal)Math.Clamp(correlation, -1d, 1d), 3)
            : null;
    }

    // ================= 基準日ごとの市場スナップショット =================

    // 実運用日もバックフィル日も必ずこの関数を通す。
    // 過去分だけ別の計算経路を用意すると、両者を比較した瞬間に意味が失われるため。
    static MarketSnapshot? BuildSnapshot(MarketDataBundle bundle, DateTime asOf, PutCallOutput putCall, bool verbose)
    {
        var spyData = TruncateTo(bundle.Sp500, asOf);
        var qqqData = TruncateTo(bundle.Nasdaq, asOf);
        if (spyData == null || qqqData == null) return null;
        if (spyData.Count < BACKFILL_MIN_HISTORY_BARS || qqqData.Count < BACKFILL_MIN_HISTORY_BARS) return null;

        var sp500 = AnalyzeIndex("S&P 500（SPY）", spyData);
        var nasdaq = AnalyzeIndex("Nasdaq-100（QQQ）", qqqData);
        if (!string.Equals(sp500.DataAsOf, nasdaq.DataAsOf, StringComparison.Ordinal)) return null;

        // 2指数のうち「悪い方（より弱気な方）」を採用するのがIBD Market Pulseの流儀
        // ※ 同順位（引き分け）のときに片方だけを「弱いから採用」と表示すると誤解を招くため、
        //    引き分けは明示的に分岐して扱う
        int Rank(string status) => status switch { "Uptrend" => 2, "Pressure" => 1, _ => 0 };
        int rankSp = Rank(sp500.StatusId);
        int rankNq = Rank(nasdaq.StatusId);
        string combinedStatus, combinedDrivenBy;
        if (rankSp == rankNq)
        {
            combinedStatus = sp500.StatusId;
            combinedDrivenBy = "SPYとQQQは同じ市場ステータス";
        }
        else if (rankSp < rankNq)
        {
            combinedStatus = sp500.StatusId;
            combinedDrivenBy = $"{sp500.Name}の状態がより弱いため採用";
        }
        else
        {
            combinedStatus = nasdaq.StatusId;
            combinedDrivenBy = $"{nasdaq.Name}の状態がより弱いため採用";
        }

        var sector = bundle.Sector == null ? null : ComputeSectorRotation(bundle.Sector, asOf);
        var sectorOutput = sector == null
            ? new SectorRotationOutput { Status = "unavailable", Note = "セクターデータを取得できませんでした。" }
            : new SectorRotationOutput
            {
                Status = "ok",
                SpyReturn1m = sector.SpyReturn1m,
                SpyReturn3m = sector.SpyReturn3m,
                DefensiveReturn1m = sector.DefensiveReturn1m,
                CyclicalReturn1m = sector.CyclicalReturn1m,
                RotationSpread1m = sector.RotationSpread1m,
                RotationSpread3m = sector.RotationSpread3m,
                Sectors = sector.Sectors,
                BreadthProxies = sector.BreadthProxies,
                Note = "SPDRセクターETF11銘柄のSPYに対する相対リターン(自前算出)。CAN SLIMの「L(Leader)」に対応する補助指標です。"
            };

        var credit = bundle.Credit == null ? null : ComputeCreditRiskAppetite(bundle.Credit, asOf);
        var creditOutput = credit == null
            ? new CreditRiskAppetiteOutput { Status = "unavailable", Note = "HYG/LQDデータを取得できませんでした。" }
            : new CreditRiskAppetiteOutput
            {
                Status = "ok",
                HygReturn1m = credit.HygReturn1m,
                HygReturn3m = credit.HygReturn3m,
                LqdReturn1m = credit.LqdReturn1m,
                LqdReturn3m = credit.LqdReturn3m,
                Spread1m = credit.Spread1m,
                Spread3m = credit.Spread3m,
                HyOasPct = credit.HyOasPct,
                HyOasChange1mBps = credit.HyOasChange1mBps,
                HyOasDate = credit.HyOasDate,
                Note = "HYGと投資適格社債ETF(LQD)の相対リターンに、ICE BofA US High Yield OASを追加しました。TLT比較より金利デュレーション差の影響を抑え、信用リスクを読み取りやすくします。"
            };

        var volatility = bundle.Volatility == null ? null : ComputeVolatilityRegime(bundle.Volatility, spyData, asOf);
        var volatilityOutput = volatility == null
            ? new VolatilityOutput { Status = "unavailable", Note = "VIXデータを取得できませんでした。" }
            : new VolatilityOutput
            {
                Status = volatility.TermSource == "VIX3M" ? "ok" : "partial",
                Vix = volatility.Vix,
                VixSma20 = volatility.VixSma20,
                Vix3m = volatility.Vix3m,
                RealizedVolShortPct = volatility.RealizedVolShortPct,
                RealizedVolLongPct = volatility.RealizedVolLongPct,
                RealizedVol21Pct = volatility.RealizedVol21Pct,
                VarianceRiskPremium = volatility.VarianceRiskPremium,
                TermSlopePct = volatility.TermSlopePct,
                TermStructure = volatility.TermStructure,
                TermSource = volatility.TermSource,
                Note = volatility.TermSource == "VIX3M"
                    ? "VIXとVIX3Mの比較による期限構造の近似です。逆転（Backwardation）は市場ストレスの警戒灯として扱い、単独の売買シグナルには使いません。"
                    : $"^VIX3Mの配信が停止しているため、SPYの{REALIZED_VOL_SHORT_WINDOW}日／{REALIZED_VOL_LONG_WINDOW}日実現ボラティリティ比で期限構造を代替しています。実現ボラの短期優勢は本物のVIX逆転より高頻度で起きるため、乖離幅に応じて段階的に警戒度を判定します。"
            };

        var breadth = bundle.Breadth == null ? null : ComputeBreadth(bundle.Breadth, asOf, verbose);
        var breadthOutput = breadth == null
            ? new MarketBreadthOutput { Status = "unavailable", Note = "Nasdaq-100構成銘柄の取得数が不足したため、真の市場ブレッドスを算出できませんでした。" }
            : new MarketBreadthOutput
            {
                Status = breadth.CoveragePct >= 95 ? "ok" : "partial",
                UniverseAsOf = breadth.UniverseAsOf,
                ExpectedConstituents = breadth.ExpectedConstituents,
                AnalyzedConstituents = breadth.AnalyzedConstituents,
                CoveragePct = breadth.CoveragePct,
                AboveSma50Pct = breadth.AboveSma50Pct,
                AboveSma200Pct = breadth.AboveSma200Pct,
                NewHighs52Week = breadth.NewHighs52Week,
                NewLows52Week = breadth.NewLows52Week,
                Advances = breadth.Advances,
                Declines = breadth.Declines,
                AdvanceDeclineNet = breadth.AdvanceDeclineNet,
                AdLineChange20d = breadth.AdLineChange20d,
                AvgPairwiseCorrelation = breadth.AvgPairwiseCorrelation,
                AccumulationPct = breadth.AccumulationPct,
                StealthDistributionPct = breadth.StealthDistributionPct,
                AdvanceRatioSma10 = breadth.AdvanceRatioSma10,
                BreadthThrustDetected = breadth.BreadthThrustDetected,
                Note = "Nasdaq-100構成銘柄を個別に集計した等ウェイトの市場内部指標です。指数上昇時でも50日線上比率やA/Dラインが悪化していれば、上昇の広がり不足を確認できます。"
            };

        return new MarketSnapshot
        {
            MarketDataAsOf = sp500.DataAsOf,
            Sp500 = sp500,
            Nasdaq = nasdaq,
            CombinedStatus = combinedStatus,
            CombinedDrivenBy = combinedDrivenBy,
            Sector = sectorOutput,
            Credit = creditOutput,
            Volatility = volatilityOutput,
            Breadth = breadthOutput,
            PutCall = putCall,
            RiskScore = CalculateMarketRiskScore(sp500, nasdaq, putCall, sectorOutput, creditOutput, volatilityOutput, breadthOutput)
        };
    }

    // 過去の各営業日についてスコアを再計算する。
    // Put/Callは過去データが取れないため、バックフィル分は常にこの項目が欠測になる（Sourceで区別）。
    static List<HistoryEntry> BuildBackfillEntries(MarketDataBundle bundle, HashSet<string> existingMarketDates)
    {
        var entries = new List<HistoryEntry>();
        var unavailablePutCall = new PutCallOutput { Status = "unavailable", Underlying = PUT_CALL_UNDERLYING };

        // 十分な履歴が確保できる日だけを対象にする（52週高値・200日線のため）。
        int start = Math.Max(BACKFILL_MIN_HISTORY_BARS - 1, bundle.Sp500.Count - BACKFILL_MAX_DAYS);
        for (int i = start; i < bundle.Sp500.Count; i++)
        {
            DateTime asOf = bundle.Sp500[i].Date.Date;
            string key = asOf.ToString("yyyy-MM-dd");
            if (existingMarketDates.Contains(key)) continue;

            var snapshot = BuildSnapshot(bundle, asOf, unavailablePutCall, verbose: false);
            if (snapshot == null || snapshot.RiskScore.AvailableMaxPoints <= 0m) continue;

            entries.Add(new HistoryEntry
            {
                Date = key,
                MarketDataAsOf = key,
                Source = "backfill",
                RubricVersion = RUBRIC_VERSION,
                CombinedStatus = snapshot.CombinedStatus,
                Sp500Status = snapshot.Sp500.StatusId,
                Sp500DistDays = snapshot.Sp500.DistributionDaysActive,
                NasdaqStatus = snapshot.Nasdaq.StatusId,
                NasdaqDistDays = snapshot.Nasdaq.DistributionDaysActive,
                MarketRiskScore = Math.Round(snapshot.RiskScore.Score, 1),
                MarketRiskAvailableMaxPoints = Math.Round(snapshot.RiskScore.AvailableMaxPoints, 1),
                SpyAdjustedClose = snapshot.Sp500.LatestAdjustedClose,
                QqqAdjustedClose = snapshot.Nasdaq.LatestAdjustedClose
            });
        }

        return entries;
    }

    // ================= 総合市場リスク・スコア =================

    static MarketRiskScore CalculateMarketRiskScore(
        IndexAnalysis sp500,
        IndexAnalysis nasdaq,
        PutCallOutput putCall,
        SectorRotationOutput sector,
        CreditRiskAppetiteOutput credit,
        VolatilityOutput volatility,
        MarketBreadthOutput breadth)
    {
        // 配点: トレンド24、ブレッドス21、機関需給9、市場構造7、ボラティリティ12、信用12、セクター10、需給5＝100。
        // 欠損データを低リスク（0点）と誤認しないよう、利用可能な配点で100点換算する。
        // 配点を変更したら MARKET_RISK_TOTAL_POINTS も必ず合わせること。
        var metrics = new List<MarketRiskMetric>();
        void Add(string group, string name, decimal score, decimal maxPoints, string detail)
        {
            metrics.Add(new MarketRiskMetric
            {
                Group = group,
                Name = name,
                Score = Math.Round(Math.Clamp(score, 0m, maxPoints), 1),
                MaxPoints = maxPoints,
                Detail = detail
            });
        }

        decimal StatusRisk(IndexAnalysis index) => index.StatusId switch
        {
            "Correction" => 7m,
            "Pressure" => 3m,
            _ => 0m
        };

        // 売り抜けの「重さ」。日数カウントとは別枠で、出来高加重の強度を評価する。
        decimal IntensityRisk(decimal intensity) =>
            intensity >= 8m ? 1.5m : intensity >= 5m ? 1m : intensity >= 2.5m ? 0.5m : 0m;

        decimal smaRisk = (sp500.IsAboveSma50 ? 0m : 2m) + (nasdaq.IsAboveSma50 ? 0m : 2m);
        decimal ddRisk = Math.Min(1.5m, sp500.DistributionDaysActive / 4m) + Math.Min(1.5m, nasdaq.DistributionDaysActive / 4m);
        Add("市場トレンド", "市場ステータス", StatusRisk(sp500) + StatusRisk(nasdaq), 14m,
            $"SPY: {sp500.StatusId} / QQQ: {nasdaq.StatusId}");
        Add("市場トレンド", "50日線", smaRisk, 4m,
            $"SPY: {(sp500.IsAboveSma50 ? "上" : "下")} / QQQ: {(nasdaq.IsAboveSma50 ? "上" : "下")}");
        Add("市場トレンド", "有効Distribution Day", ddRisk, 3m,
            $"SPY: {sp500.DistributionDaysActive}日 / QQQ: {nasdaq.DistributionDaysActive}日");
        Add("市場トレンド", "売り抜け強度", IntensityRisk(sp500.DistributionIntensity) + IntensityRisk(nasdaq.DistributionIntensity), 3m,
            $"SPY: {sp500.DistributionIntensity:F1} / QQQ: {nasdaq.DistributionIntensity:F1}（下落率×出来高比の合計）");

        if (breadth.Status is "ok" or "partial")
        {
            if (breadth.AboveSma50Pct.HasValue)
            {
                Add("市場ブレッドス", "50日線上比率", BreadthPctRisk(breadth.AboveSma50Pct.Value, 6m), 6m,
                    $"{breadth.AboveSma50Pct.Value:F1}%");
            }
            if (breadth.AboveSma200Pct.HasValue)
            {
                Add("市場ブレッドス", "200日線上比率", BreadthPctRisk(breadth.AboveSma200Pct.Value, 5m), 5m,
                    $"{breadth.AboveSma200Pct.Value:F1}%");
            }

            decimal highLowRisk = breadth.NewLows52Week > breadth.NewHighs52Week ? 4m :
                breadth.NewLows52Week > 0 && breadth.NewHighs52Week < breadth.NewLows52Week * 2 ? 2.5m : 0m;
            Add("市場ブレッドス", "52週新高値・新安値", highLowRisk, 4m,
                $"新高値 {breadth.NewHighs52Week} / 新安値 {breadth.NewLows52Week}");

            decimal adRisk = breadth.AdLineChange20d <= -150 ? 3m : breadth.AdLineChange20d < 0 ? 1.5m : 0m;
            Add("市場ブレッドス", "20日A/Dライン", adRisk, 3m,
                $"20日変化 {(breadth.AdLineChange20d > 0 ? "+" : "")}{breadth.AdLineChange20d}");

            if (breadth.AdvanceRatioSma10.HasValue)
            {
                // 騰落レシオが低いまま張り付く＝売りが広く持続している状態。
                // Breadth Thrust点灯時はレシオ自体が0.615超なので自動的に0点になる。
                decimal ratio = breadth.AdvanceRatioSma10.Value;
                decimal ratioRisk = ratio <= 0.35m ? 3m : ratio <= 0.42m ? 2m : ratio <= 0.48m ? 1m : 0m;
                string thrustNote = breadth.BreadthThrustDetected ? " / Zweig Breadth Thrust 点灯" : "";
                Add("市場ブレッドス", "10日騰落レシオ", ratioRisk, 3m, $"{ratio:F3}{thrustNote}");
            }

            if (breadth.AccumulationPct.HasValue)
            {
                // 「かろうじて半数」では健全とは言えないため、55%未満から軽い加点を始める。
                decimal accumulation = breadth.AccumulationPct.Value;
                decimal accumulationRisk = accumulation < 35m ? 5m : accumulation < 45m ? 3.5m : accumulation < 55m ? 2m : 0m;
                Add("機関需給", "アキュムレーション銘柄比率", accumulationRisk, 5m,
                    $"{accumulation:F1}%（50日の出来高が上昇日に偏る銘柄）");
            }

            if (breadth.StealthDistributionPct.HasValue)
            {
                // 価格は50日線の上なのに出来高は下落日に偏る＝チャートだけ見ていると気づけない売り抜け。
                decimal stealth = breadth.StealthDistributionPct.Value;
                decimal stealthRisk = stealth >= 40m ? 4m : stealth >= 25m ? 2.5m : stealth >= 15m ? 1m : 0m;
                Add("機関需給", "ステルス配分", stealthRisk, 4m,
                    $"{stealth:F1}%（50日線上だが売り優勢）");
            }

            if (breadth.AvgPairwiseCorrelation.HasValue)
            {
                // 閾値の根拠：NDX全構成銘柄（セクター混在）の21日平均ペア相関は、分散が効く平時で0.05〜0.20、
                // 平常0.20〜0.35、リスクオフ0.40〜0.60、パニックで0.60超という分布になる。
                // 大型テック20銘柄だけに絞ると同時点で0.18と高く出るため、全銘柄ベースの水準で判定する。
                // ※ここは自己履歴が貯まるまで検証できない、最も暫定的な閾値。
                decimal correlation = breadth.AvgPairwiseCorrelation.Value;
                decimal correlationRisk = correlation >= 0.55m ? 7m : correlation >= 0.40m ? 5m : correlation >= 0.25m ? 2m : 0m;
                Add("市場構造", "銘柄間相関", correlationRisk, 7m,
                    $"平均ペア相関 {correlation:F3}（{CORRELATION_WINDOW}日）");
            }
        }

        if (volatility.Status is "ok" or "partial" && volatility.Vix.HasValue && volatility.VixSma20.HasValue && volatility.TermSlopePct.HasValue)
        {
            decimal vixRisk = volatility.Vix.Value > volatility.VixSma20.Value * 1.2m ? 4m :
                volatility.Vix.Value > volatility.VixSma20.Value ? 2.5m : 0m;
            Add("ボラティリティ", "VIX対20日平均", vixRisk, 4m,
                $"VIX {volatility.Vix.Value:F2} / 20日平均 {volatility.VixSma20.Value:F2}");
            decimal termRisk;
            string slopeLabel;
            if (volatility.TermSource == "VIX3M")
            {
                slopeLabel = "VIX対VIX3M";
                termRisk = volatility.TermStructure == "Backwardation" ? 5m : 0m;
            }
            else
            {
                // 実現ボラの「短期>長期」は本物のVIX逆転よりはるかに高頻度で起きる。
                // 同じ条件で満点を与えると平常時でもリスクを過大評価するため、乖離幅で段階配点する。
                slopeLabel = "SPY実現ボラ 短期対長期";
                decimal slope = volatility.TermSlopePct.Value;
                termRisk = slope >= 20m ? 5m : slope >= 5m ? 2.5m : 0m;
            }
            Add("ボラティリティ", "VIX期限構造", termRisk, 5m,
                $"{volatility.TermStructure}（{slopeLabel} {volatility.TermSlopePct.Value:+0.00;-0.00;0.00}%）");

            if (volatility.VarianceRiskPremium.HasValue)
            {
                // マイナス＝実現ボラがインプライドを上回る＝現実の変動に市場が追いつけていない。
                decimal vrp = volatility.VarianceRiskPremium.Value;
                decimal vrpRisk = vrp <= -2m ? 3m : vrp <= 0m ? 2m : vrp <= 1.5m ? 1m : 0m;
                Add("ボラティリティ", "分散リスクプレミアム", vrpRisk, 3m,
                    $"{vrp:+0.00;-0.00;0.00}（VIX {volatility.Vix.Value:F2} − 実現ボラ {volatility.RealizedVol21Pct:F2}）");
            }
        }

        if (credit.Status == "ok")
        {
            if (credit.Spread3m.HasValue)
            {
                decimal creditRisk = credit.Spread3m.Value <= -5m ? 6m : credit.Spread3m.Value < 0m ? 3.5m : 0m;
                Add("クレジット", "HY対IG相対リターン", creditRisk, 6m,
                    $"3か月 {credit.Spread3m.Value:+0.00;-0.00;0.00}%");
            }
            if (credit.HyOasPct.HasValue)
            {
                decimal oasLevelRisk = credit.HyOasPct.Value >= 6m ? 3m : credit.HyOasPct.Value >= 4.5m ? 1.5m : 0m;
                Add("クレジット", "HY OAS水準", oasLevelRisk, 3m,
                    $"{credit.HyOasPct.Value:F2}%");
            }
            if (credit.HyOasChange1mBps.HasValue)
            {
                decimal oasChangeRisk = credit.HyOasChange1mBps.Value >= 75m ? 3m : credit.HyOasChange1mBps.Value >= 25m ? 1.5m : 0m;
                Add("クレジット", "HY OAS 1か月変化", oasChangeRisk, 3m,
                    $"{credit.HyOasChange1mBps.Value:+0;-0;0}bp");
            }
        }

        if (sector.Status == "ok" && sector.Sectors != null)
        {
            int positiveSectors = sector.Sectors.Count(s => s.RelStrength3m > 0m);
            decimal sectorRisk = positiveSectors >= 7 ? 0m : positiveSectors >= 5 ? 1.5m : positiveSectors >= 3 ? 2.5m : 4m;
            Add("セクター", "対SPYで優位なセクター数", sectorRisk, 4m,
                $"{positiveSectors}/{sector.Sectors.Count}セクター");

            if (sector.BreadthProxies != null && sector.BreadthProxies.Count > 0)
            {
                decimal proxyMax = sector.BreadthProxies.Count * 1.5m;
                decimal proxyRisk = sector.BreadthProxies.Sum(proxy => proxy.RelStrength3m <= -3m ? 1.5m : proxy.RelStrength3m < 0m ? 0.75m : 0m);
                Add("セクター", "RSP・IWMの相対強度", proxyRisk, proxyMax,
                    string.Join(" / ", sector.BreadthProxies.Select(p => $"{p.Symbol} {p.RelStrength3m:+0.00;-0.00;0.00}%")));
            }

            if (sector.RotationSpread1m.HasValue)
            {
                // ディフェンシブがシクリカルを上回る幅が大きいほど、資金が守りへ退避している。
                decimal rotation = sector.RotationSpread1m.Value;
                decimal rotationRisk = rotation >= 4m ? 3m : rotation >= 1.5m ? 2m : rotation >= 0m ? 1m : 0m;
                Add("セクター", "ディフェンシブ優位度", rotationRisk, 3m,
                    $"1か月 {rotation:+0.00;-0.00;0.00}%（守り {sector.DefensiveReturn1m:F2}% 対 攻め {sector.CyclicalReturn1m:F2}%）");
            }
        }

        if (putCall.Status == "ok" && putCall.PercentileRank.HasValue)
        {
            decimal percentile = (decimal)putCall.PercentileRank.Value;
            decimal putCallRisk = percentile <= 10m || percentile >= 90m ? 5m :
                percentile <= 20m || percentile >= 80m ? 3m : 0m;
            Add("需給", "Put/Call極端値", putCallRisk, 5m,
                $"自己履歴の{percentile:F1}パーセンタイル");
        }

        decimal availableMax = metrics.Sum(metric => metric.MaxPoints);
        decimal rawScore = metrics.Sum(metric => metric.Score);
        decimal normalizedScore = availableMax > 0m ? Math.Round(rawScore / availableMax * 100m, 1) : 0m;
        string label = normalizedScore switch
        {
            <= 20m => "良好",
            <= 40m => "概ね良好",
            <= 60m => "注意",
            <= 80m => "警戒",
            _ => "高リスク"
        };

        return new MarketRiskScore
        {
            Score = normalizedScore,
            RawScore = Math.Round(rawScore, 1),
            AvailableMaxPoints = Math.Round(availableMax, 1),
            // 配点合計と満点がたまたま一致していたため、これまで生の配点をそのまま「%」として表示していた。
            // 満点で割った本来のカバレッジ率にする。
            DataCoveragePct = Math.Round(Math.Min(100m, availableMax / MARKET_RISK_TOTAL_POINTS * 100m), 1),
            Label = label,
            Metrics = metrics
        };
    }

    static decimal BreadthPctRisk(decimal pct, decimal maxPoints) => pct switch
    {
        < 30m => maxPoints,
        < 45m => Math.Round(maxPoints * 0.7m, 1),
        < 60m => Math.Round(maxPoints * 0.3m, 1),
        _ => 0m
    };

    // ================= 指数ごとの分析 =================

    static IndexAnalysis AnalyzeIndex(string name, List<DailyData> data)
    {
        int n = data.Count;

        // --- 50-SMA（各日について、その日を含む直近50日の平均） ---
        // ※ Skip/Take/Averageを毎回回すと日数分×50回の走査とLINQの列挙用オブジェクト生成が発生するため、
        //    直近50日分の合計を保持しながら1日ずつスライドさせるO(n)の実装に変更（アロケーションもほぼゼロに）
        var sma50 = new decimal?[n];
        if (n >= 50)
        {
            decimal windowSum = 0;
            for (int i = 0; i < 50; i++) windowSum += data[i].AdjustedClose;
            sma50[49] = Math.Round(windowSum / 50m, 2);
            for (int i = 50; i < n; i++)
            {
                windowSum += data[i].AdjustedClose - data[i - 50].AdjustedClose;
                sma50[i] = Math.Round(windowSum / 50m, 2);
            }
        }

        // --- 売り抜け日の「生」判定を先に1回だけ計算 ---
        // チャート表示用マーカーとアクティブ集計の両方でこの結果を使い回すことで、
        // 判定式が2箇所に重複してどちらか一方だけ修正され矛盾する事故を防ぐ
        var isRawDistDay = new bool[n];
        for (int i = 1; i < n; i++)
        {
            decimal dropPct = (data[i - 1].AdjustedClose - data[i].AdjustedClose) / data[i - 1].AdjustedClose * 100m;
            isRawDistDay[i] = dropPct >= DIST_DAY_DROP_PCT && data[i].Volume > data[i - 1].Volume;
        }

        // --- 売り抜け日（失効ルール込みでアクティブな件数を日次で追跡） ---
        var distDaysActive = new int[n];
        var activeDDs = new List<(int idx, decimal close)>();
        for (int i = 1; i < n; i++)
        {
            // 25営業日経過 または そのDDの終値から5%以上反発 したものは無効化
            activeDDs.RemoveAll(dd => (i - dd.idx) > DIST_DAY_WINDOW || data[i].AdjustedClose >= dd.close * (1 + DIST_DAY_INVALIDATE_RALLY_PCT / 100m));

            if (isRawDistDay[i])
            {
                activeDDs.Add((i, data[i].AdjustedClose));
            }
            distDaysActive[i] = activeDDs.Count;
        }

        // ループ終了時点のactiveDDsが「最新日時点でアクティブな売り抜け日」そのもの。
        // チャートでの有効/失効の視覚区別と、深刻さ（最大下落率）の算出に使う
        var activeDDIndices = activeDDs.Select(dd => dd.idx).ToHashSet();

        // --- 売り抜け日の「強度」（出来高加重） ---
        // 日数カウントだけでは -0.3%×平常出来高 の日と -2.0%×1.8倍出来高 の日が同じ「1日」になる。
        // 下落率×出来高比で重み付けし、機関の売りの本気度を数値化する。
        var volumeSma50 = new decimal?[n];
        if (n >= 50)
        {
            decimal volumeWindowSum = 0m;
            for (int i = 0; i < 50; i++) volumeWindowSum += data[i].Volume;
            volumeSma50[49] = volumeWindowSum / 50m;
            for (int i = 50; i < n; i++)
            {
                volumeWindowSum += data[i].Volume - data[i - 50].Volume;
                volumeSma50[i] = volumeWindowSum / 50m;
            }
        }

        decimal distributionIntensity = 0m;
        foreach (var (idx, _) in activeDDs)
        {
            decimal dropPct = (data[idx - 1].AdjustedClose - data[idx].AdjustedClose) / data[idx - 1].AdjustedClose * 100m;
            decimal volumeRatio = volumeSma50[idx].HasValue && volumeSma50[idx]!.Value > 0m
                ? data[idx].Volume / volumeSma50[idx]!.Value
                : 1m;
            distributionIntensity += dropPct * volumeRatio;
        }
        distributionIntensity = Math.Round(distributionIntensity, 2);

        decimal? worstActiveDropPct = null;
        string? worstActiveDropDate = null;
        if (activeDDs.Count > 0)
        {
            int worstIdx = activeDDs
                .Select(dd => dd.idx)
                .OrderByDescending(idx => (data[idx - 1].AdjustedClose - data[idx].AdjustedClose) / data[idx - 1].AdjustedClose)
                .First();
            worstActiveDropPct = Math.Round((data[worstIdx - 1].AdjustedClose - data[worstIdx].AdjustedClose) / data[worstIdx - 1].AdjustedClose * 100m, 2);
            worstActiveDropDate = data[worstIdx].Date.ToString("yyyy-MM-dd");
        }

        // --- ラリー・アテンプト / フォロースルーデー のステートマシン ---
        var states = new string[n];
        states[0] = "Correction";
        string currentState = "Correction";
        int? day1Index = null;
        decimal? day1Low = null;
        DateTime? lastFtdDate = null;

        for (int i = 1; i < n; i++)
        {
            // Confirmed Uptrend からの「格下げ」判定（50日線割れ + 売り抜け日蓄積）
            if (currentState == "ConfirmedUptrend" && sma50[i].HasValue)
            {
                bool aboveSma = data[i].AdjustedClose >= sma50[i]!.Value;
                if (!aboveSma && distDaysActive[i] >= DIST_DAY_BREAKDOWN_THRESHOLD)
                {
                    currentState = "Correction";
                    day1Index = null;
                    day1Low = null;
                }
            }

            if (currentState != "ConfirmedUptrend")
            {
                if (day1Index == null)
                {
                    // Day1候補：前日が直近10日の安値で、当日が陽転した日
                    // （LINQのSkip/Take/Minは短い区間でも毎回列挙用オブジェクトを作るため、単純なforループで代替）
                    int lookback = Math.Max(0, i - DAY1_LOOKBACK);
                    decimal recentLow = data[lookback].AdjustedClose;
                    for (int k = lookback + 1; k < i; k++)
                    {
                        if (data[k].AdjustedClose < recentLow) recentLow = data[k].AdjustedClose;
                    }
                    if (data[i].AdjustedClose > data[i - 1].AdjustedClose && data[i - 1].AdjustedClose <= recentLow)
                    {
                        day1Index = i;
                        day1Low = data[i - 1].AdjustedClose;
                        currentState = "RallyAttempt";
                    }
                }
                else
                {
                    if (data[i].AdjustedClose < day1Low!.Value)
                    {
                        // アンダーカット：ラリー失敗。仕切り直し（次の陽転日を新たなDay1候補として探す）
                        day1Index = null;
                        day1Low = null;
                        currentState = "Correction";
                    }
                    else
                    {
                        int attemptDay = i - day1Index.Value;
                        if (attemptDay > FTD_MAX_DAY)
                        {
                            day1Index = null;
                            day1Low = null;
                            currentState = "Correction";
                        }
                        else if (attemptDay >= FTD_MIN_DAY)
                        {
                            decimal gainPct = (data[i].AdjustedClose - data[i - 1].AdjustedClose) / data[i - 1].AdjustedClose * 100m;
                            if (gainPct >= FTD_GAIN_PCT && data[i].Volume > data[i - 1].Volume)
                            {
                                currentState = "ConfirmedUptrend";
                                lastFtdDate = data[i].Date;
                            }
                        }
                    }
                }
            }

            states[i] = currentState;
        }

        // --- 最新日のステータス判定 ---
        string lastState = states[n - 1];
        int lastDistDays = distDaysActive[n - 1];
        string statusId = lastState == "ConfirmedUptrend"
            ? (lastDistDays < DIST_DAY_PRESSURE_THRESHOLD ? "Uptrend" : "Pressure")
            : "Correction";

        bool isAboveSma50 = sma50[n - 1].HasValue && data[n - 1].AdjustedClose >= sma50[n - 1]!.Value;

        // --- 52週高値からのドローダウン（取得済みの1年分データからそのまま算出、追加取得コスト無し） ---
        decimal high52Week = data.Max(d => d.AdjustedClose);
        decimal drawdownFromHighPct = Math.Round((data[n - 1].AdjustedClose - high52Week) / high52Week * 100m, 2);

        // --- チャート表示用（直近100日） ---
        int chartStart = Math.Max(0, n - 100);
        var chartLabels = new List<string>();
        var chartPrices = new List<decimal>();
        var chartSma50 = new List<decimal?>();
        var distMarksActive = new List<decimal?>();
        var distMarksExpired = new List<decimal?>();

        for (int i = chartStart; i < n; i++)
        {
            chartLabels.Add(data[i].Date.ToString("MM-dd"));
            chartPrices.Add(data[i].AdjustedClose);
            chartSma50.Add(sma50[i]);
            // 同じ「売り抜け日」でも、最新日時点でまだアクティブなものと、
            // 25日経過または5%反発で既に失効したものを別データセットとして分ける
            bool isActive = isRawDistDay[i] && activeDDIndices.Contains(i);
            bool isExpired = isRawDistDay[i] && !activeDDIndices.Contains(i);
            distMarksActive.Add(isActive ? data[i].AdjustedClose : (decimal?)null);
            distMarksExpired.Add(isExpired ? data[i].AdjustedClose : (decimal?)null);
        }

        return new IndexAnalysis
        {
            Name = name,
            DataAsOf = data[n - 1].Date.ToString("yyyy-MM-dd"),
            LatestAdjustedClose = data[n - 1].AdjustedClose,
            Sma50 = sma50[n - 1],
            IsAboveSma50 = isAboveSma50,
            High52WeekAdjusted = high52Week,
            DrawdownFromHighPct = drawdownFromHighPct,
            DistributionDaysActive = lastDistDays,
            DistributionIntensity = distributionIntensity,
            WorstActiveDropPct = worstActiveDropPct,
            WorstActiveDropDate = worstActiveDropDate,
            TrendState = lastState,
            LastFollowThroughDate = lastFtdDate?.ToString("yyyy-MM-dd"),
            StatusId = statusId,
            Chart = new ChartData
            {
                Labels = chartLabels,
                Prices = chartPrices,
                Sma50 = chartSma50,
                DistMarksActive = distMarksActive,
                DistMarksExpired = distMarksExpired
            }
        };
    }

    // ================= 履歴保存 =================

    static HistoryPreparation PrepareHistory(string combinedStatus, IndexAnalysis sp500, IndexAnalysis nasdaq, decimal? putCallRatio, string marketDataAsOf)
    {
        string historyPath = GetOutputPath("history.json");

        List<HistoryEntry> history = new();
        if (File.Exists(historyPath))
        {
            var existing = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(historyPath), JsonOptions)
                ?? throw new InvalidDataException("history.json が空または配列ではありません。ファイルを確認してください。");
            if (existing.Any(entry => !IsValidHistoryEntry(entry)))
                throw new InvalidDataException("history.json に不正な日付・市場ステータス・数値が含まれています。更新を中止しました。");
            if (existing.Select(entry => entry.Date).Distinct(StringComparer.Ordinal).Count() != existing.Count)
                throw new InvalidDataException("history.json に同じ日付の重複があります。更新を中止しました。");

            history = existing.OrderBy(entry => entry.Date).ToList();
        }

        string today = JstNow().ToString("yyyy-MM-dd");
        history.RemoveAll(h => h.Date == today); // 同日再実行時は上書き

        history.Add(new HistoryEntry
        {
            Date = today,
            MarketDataAsOf = marketDataAsOf,
            Source = "live",
            RubricVersion = RUBRIC_VERSION,
            CombinedStatus = combinedStatus,
            Sp500Status = sp500.StatusId,
            Sp500DistDays = sp500.DistributionDaysActive,
            NasdaqStatus = nasdaq.StatusId,
            NasdaqDistDays = nasdaq.DistributionDaysActive,
            PutCallRatio = putCallRatio,
            SpyAdjustedClose = sp500.LatestAdjustedClose,
            QqqAdjustedClose = nasdaq.LatestAdjustedClose
        });

        // ここではまだファイルへ書かず、全計算成功後に保存する。
        var trimmed = history.OrderBy(h => h.Date, StringComparer.Ordinal).TakeLast(HISTORY_MAX_ENTRIES).ToList();

        // --- Put/Call Ratioのトレンド統計（当日の候補値を含めてメモリ上で算出する） ---
        var validRatios = trimmed.Where(h => h.PutCallRatio.HasValue).Select(h => h.PutCallRatio!.Value).ToList();

        // 履歴が1日しかない状態で平均値を出すと「10日平均」という表示が実態と食い違うため、
        // 窓が埋まるまではnullにして未確定であることを示す。
        decimal? sma = validRatios.Count >= PUT_CALL_SMA_WINDOW
            ? Math.Round(validRatios.TakeLast(PUT_CALL_SMA_WINDOW).Average(), 3)
            : null;

        double? percentile = null;
        if (putCallRatio.HasValue && validRatios.Count >= PUT_CALL_MIN_HISTORY_FOR_PERCENTILE)
        {
            // 「自分以下の値が全体の何%を占めるか」＝高いほどプット優勢（弱気/ヘッジ需要が強い）な極値に近い
            int countAtOrBelow = validRatios.Count(v => v <= putCallRatio.Value);
            percentile = Math.Round((double)countAtOrBelow / validRatios.Count * 100.0, 1);
        }

        return new HistoryPreparation
        {
            Entries = trimmed,
            PutCallStats = new PutCallStats { Sma10 = sma, PercentileRank = percentile, HistoryDays = validRatios.Count }
        };
    }

    static bool IsValidHistoryEntry(HistoryEntry entry)
    {
        bool validDate = DateTime.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        bool validMarketDataDate = string.IsNullOrEmpty(entry.MarketDataAsOf) ||
            DateTime.TryParseExact(entry.MarketDataAsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        bool validStatus = entry.CombinedStatus is "Correction" or "Pressure" or "Uptrend" &&
            entry.Sp500Status is "Correction" or "Pressure" or "Uptrend" &&
            entry.NasdaqStatus is "Correction" or "Pressure" or "Uptrend";
        bool validNumbers = entry.Sp500DistDays >= 0 && entry.NasdaqDistDays >= 0 &&
            (!entry.PutCallRatio.HasValue || (entry.PutCallRatio.Value > 0m && entry.PutCallRatio.Value <= 100m)) &&
            (!entry.MarketRiskScore.HasValue || (entry.MarketRiskScore.Value >= 0m && entry.MarketRiskScore.Value <= 100m)) &&
            (!entry.MarketRiskAvailableMaxPoints.HasValue || (entry.MarketRiskAvailableMaxPoints.Value > 0m && entry.MarketRiskAvailableMaxPoints.Value <= 100m)) &&
            (!entry.SpyAdjustedClose.HasValue || entry.SpyAdjustedClose.Value > 0m) &&
            (!entry.QqqAdjustedClose.HasValue || entry.QqqAdjustedClose.Value > 0m) &&
            IsValidForwardReturn(entry.SpyReturn1m) && IsValidForwardReturn(entry.QqqReturn1m) &&
            IsValidForwardReturn(entry.SpyReturn3m) && IsValidForwardReturn(entry.QqqReturn3m) &&
            IsValidDrawdown(entry.SpyMaxDrawdown1m) && IsValidDrawdown(entry.QqqMaxDrawdown1m) &&
            IsValidDrawdown(entry.SpyMaxDrawdown3m) && IsValidDrawdown(entry.QqqMaxDrawdown3m) &&
            (entry.MarketRiskMetrics == null || entry.MarketRiskMetrics.All(IsValidMarketRiskMetric));
        return validDate && validMarketDataDate && validStatus && validNumbers;
    }

    static bool IsValidForwardReturn(decimal? value) => !value.HasValue || (value.Value > -100m && value.Value <= 1000m);

    static bool IsValidDrawdown(decimal? value) => !value.HasValue || (value.Value >= -100m && value.Value <= 0m);

    static bool IsValidMarketRiskMetric(MarketRiskMetric metric) =>
        !string.IsNullOrWhiteSpace(metric.Group) && !string.IsNullOrWhiteSpace(metric.Name) &&
        metric.Score >= 0m && metric.MaxPoints > 0m && metric.Score <= metric.MaxPoints;

    static void ApplyTodayRiskScoreSnapshot(List<HistoryEntry> history, MarketRiskScore riskScore)
    {
        if (riskScore.Score < 0m || riskScore.Score > 100m || riskScore.AvailableMaxPoints <= 0m)
            throw new ArgumentOutOfRangeException(nameof(riskScore), "市場リスクスコアまたは採点カバレッジが不正です。");

        string today = JstNow().ToString("yyyy-MM-dd");
        var todayEntry = history.SingleOrDefault(entry => entry.Date == today)
            ?? throw new InvalidDataException("当日の履歴が見つからないため、市場リスクスコアを保存できません。");
        todayEntry.MarketRiskScore = Math.Round(riskScore.Score, 1);
        todayEntry.MarketRiskAvailableMaxPoints = Math.Round(riskScore.AvailableMaxPoints, 1);
        todayEntry.MarketRiskMetrics = riskScore.Metrics.Select(CopyMarketRiskMetric).ToList();
    }

    static MarketRiskMetric CopyMarketRiskMetric(MarketRiskMetric metric) => new()
    {
        Group = metric.Group,
        Name = metric.Name,
        Score = metric.Score,
        MaxPoints = metric.MaxPoints,
        Detail = metric.Detail
    };

    static void UpdateScoreValidationOutcomes(List<HistoryEntry> history, List<DailyData> sp500Data, List<DailyData> nasdaqData)
    {
        foreach (var entry in history)
        {
            if (string.IsNullOrEmpty(entry.MarketDataAsOf) || !entry.SpyAdjustedClose.HasValue || !entry.QqqAdjustedClose.HasValue)
                continue; // 機能追加前の履歴には基準価格がないため、推測で補完しない。

            UpdateHorizonOutcome(
                sp500Data, entry.MarketDataAsOf, SCORE_VALIDATION_1M_DAYS,
                value => entry.SpyReturn1m = value, value => entry.SpyMaxDrawdown1m = value, entry.SpyReturn1m.HasValue);
            UpdateHorizonOutcome(
                nasdaqData, entry.MarketDataAsOf, SCORE_VALIDATION_1M_DAYS,
                value => entry.QqqReturn1m = value, value => entry.QqqMaxDrawdown1m = value, entry.QqqReturn1m.HasValue);
            UpdateHorizonOutcome(
                sp500Data, entry.MarketDataAsOf, SCORE_VALIDATION_3M_DAYS,
                value => entry.SpyReturn3m = value, value => entry.SpyMaxDrawdown3m = value, entry.SpyReturn3m.HasValue);
            UpdateHorizonOutcome(
                nasdaqData, entry.MarketDataAsOf, SCORE_VALIDATION_3M_DAYS,
                value => entry.QqqReturn3m = value, value => entry.QqqMaxDrawdown3m = value, entry.QqqReturn3m.HasValue);
        }
    }

    static void UpdateHorizonOutcome(
        List<DailyData> data,
        string marketDataAsOf,
        int horizonDays,
        Action<decimal> setReturn,
        Action<decimal> setMaxDrawdown,
        bool alreadyMatured)
    {
        if (alreadyMatured) return;

        if (!DateTime.TryParseExact(marketDataAsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var baseDate))
            return;

        int startIndex = data.FindIndex(day => day.Date.Date == baseDate.Date);
        if (startIndex < 0) return;
        int targetIndex = startIndex + horizonDays;
        if (targetIndex >= data.Count) return;

        // 調整後終値は分配金が出るたびに過去分が遡って再計算される。
        // 履歴に記録した当時の終値を分母にすると、その後の分配金の分だけリターンが過大評価される。
        // 分子・分母を同じ取得回の系列でそろえて、この歪みを消す。
        decimal baseClose = data[startIndex].AdjustedClose;
        if (baseClose <= 0m) return;

        decimal futureClose = data[targetIndex].AdjustedClose;
        decimal maxDrawdown = data.Skip(startIndex).Take(horizonDays + 1)
            .Min(day => (day.AdjustedClose / baseClose - 1m) * 100m);
        setReturn(Math.Round((futureClose / baseClose - 1m) * 100m, 2));
        setMaxDrawdown(Math.Round(maxDrawdown, 2));
    }

    static MarketRiskChange BuildMarketRiskChange(List<HistoryEntry> history)
    {
        string today = JstNow().ToString("yyyy-MM-dd");
        var current = history.SingleOrDefault(entry => entry.Date == today);
        // 週末や休日に再実行すると市場基準日が前回と同じになる。
        // その比較は必ず「変化なし」になり、相場が動いていないかのように読めてしまうため、
        // 市場基準日が実際に進んでいる直近の記録とだけ比較する。
        var previous = history
            .Where(entry => entry.Date != today && entry.MarketRiskScore.HasValue &&
                !string.Equals(entry.MarketDataAsOf, current?.MarketDataAsOf, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Date, StringComparer.Ordinal).FirstOrDefault();

        if (current?.MarketRiskScore == null || previous?.MarketRiskScore == null ||
            current.MarketRiskMetrics == null || previous.MarketRiskMetrics == null ||
            current.MarketRiskMetrics.Count == 0 || previous.MarketRiskMetrics.Count == 0 ||
            !current.MarketRiskAvailableMaxPoints.HasValue || !previous.MarketRiskAvailableMaxPoints.HasValue)
        {
            return new MarketRiskChange
            {
                Status = "collecting",
                Note = "前回分の採点内訳がそろうと、前回からの変動理由を表示します。"
            };
        }

        var previousMetrics = previous.MarketRiskMetrics.ToDictionary(
            metric => $"{metric.Group}\u001f{metric.Name}", StringComparer.Ordinal);
        var factors = new List<MarketRiskChangeFactor>();
        var currentKeys = new HashSet<(string Group, string Name)>();
        foreach (var metric in current.MarketRiskMetrics)
        {
            currentKeys.Add((metric.Group, metric.Name));
            previousMetrics.TryGetValue($"{metric.Group}\u001f{metric.Name}", out var prior);

            // 前回に無い項目を捨てると、指標が復旧・欠落した日の変動理由が空欄になってしまう。
            // 片側にしか存在しない項目も、その寄与分をそのまま変化として並べる。
            decimal currentContribution = metric.Score / current.MarketRiskAvailableMaxPoints.Value * 100m;
            decimal previousContribution = prior == null
                ? 0m
                : prior.Score / previous.MarketRiskAvailableMaxPoints.Value * 100m;
            decimal delta = Math.Round(currentContribution - previousContribution, 1);
            if (Math.Abs(delta) < 0.1m) continue;

            factors.Add(new MarketRiskChangeFactor
            {
                Group = metric.Group,
                Name = metric.Name,
                ChangeInRiskPoints = delta,
                CurrentDetail = metric.Detail,
                PreviousDetail = prior?.Detail ?? "前回は未取得"
            });
        }

        foreach (var prior in previous.MarketRiskMetrics)
        {
            if (currentKeys.Contains((prior.Group, prior.Name))) continue;

            decimal delta = Math.Round(-(prior.Score / previous.MarketRiskAvailableMaxPoints.Value * 100m), 1);
            if (Math.Abs(delta) < 0.1m) continue;

            factors.Add(new MarketRiskChangeFactor
            {
                Group = prior.Group,
                Name = prior.Name,
                ChangeInRiskPoints = delta,
                CurrentDetail = "今回は未取得",
                PreviousDetail = prior.Detail
            });
        }

        decimal scoreChange = Math.Round(current.MarketRiskScore.Value - previous.MarketRiskScore.Value, 1);
        decimal coverageChange = Math.Round(current.MarketRiskAvailableMaxPoints.Value - previous.MarketRiskAvailableMaxPoints.Value, 1);
        string note = coverageChange == 0m
            ? "前回と共通する採点項目を、100点換算で比較しています。"
            : $"採点カバレッジも前回から{coverageChange:+0.0;-0.0;0.0}ポイント変化しています。欠損項目による単純比較には注意してください。";

        return new MarketRiskChange
        {
            Status = "ok",
            PreviousDate = previous.Date,
            PreviousScore = previous.MarketRiskScore,
            ScoreChange = scoreChange,
            CoverageChange = coverageChange,
            Factors = factors.OrderByDescending(factor => Math.Abs(factor.ChangeInRiskPoints)).Take(4).ToList(),
            Note = note
        };
    }

    static ScoreValidationOutput BuildScoreValidation(List<HistoryEntry> history)
    {
        // 同じ市場基準日を複数回更新した場合は最後の記録だけを使い、休日・再実行による重複集計を防ぐ。
        // 配点体系が違うスコアは同じ箱に入れない（分母が変わると同じ点数でも意味が変わるため）。
        var observations = history
            .Where(entry => entry.MarketRiskScore.HasValue && !string.IsNullOrEmpty(entry.MarketDataAsOf) &&
                entry.SpyAdjustedClose.HasValue && entry.QqqAdjustedClose.HasValue &&
                (entry.RubricVersion ?? 0) == RUBRIC_VERSION)
            .GroupBy(entry => entry.MarketDataAsOf!, StringComparer.Ordinal)
            .Select(group => group.OrderBy(entry => entry.Date).Last())
            .OrderBy(entry => entry.MarketDataAsOf)
            .ToList();
        int backfilledCount = observations.Count(entry => entry.Source == "backfill");

        var bands = new[]
        {
            (Label: "0–20", Matches: new Func<decimal, bool>(score => score <= 20m)),
            (Label: "21–40", Matches: new Func<decimal, bool>(score => score > 20m && score <= 40m)),
            (Label: "41–60", Matches: new Func<decimal, bool>(score => score > 40m && score <= 60m)),
            (Label: "61–80", Matches: new Func<decimal, bool>(score => score > 60m && score <= 80m)),
            (Label: "81–100", Matches: new Func<decimal, bool>(score => score > 80m))
        };

        var bandResults = bands.Select(band => BuildScoreBandValidation(
            band.Label, observations.Where(entry => band.Matches(entry.MarketRiskScore!.Value)).ToList())).ToList();
        int oneMonthMatured = observations.Count(entry => entry.SpyReturn1m.HasValue && entry.QqqReturn1m.HasValue);
        int threeMonthMatured = observations.Count(entry => entry.SpyReturn3m.HasValue && entry.QqqReturn3m.HasValue);
        string status = oneMonthMatured >= SCORE_VALIDATION_RECOMMENDED_MIN_SAMPLES ? "preliminary" : "collecting";

        return new ScoreValidationOutput
        {
            Status = status,
            ObservationCount = observations.Count,
            OneMonthMaturedCount = oneMonthMatured,
            ThreeMonthMaturedCount = threeMonthMatured,
            RecommendedMinSamples = SCORE_VALIDATION_RECOMMENDED_MIN_SAMPLES,
            BackfilledCount = backfilledCount,
            Bands = bandResults,
            Note = backfilledCount == 0
                ? "各スコア記録日の調整後終値から21・63営業日後までの実績です。過去実績であり、将来のリターンや投資成果を保証するものではありません。"
                : $"{observations.Count}件中{backfilledCount}件は過去データからの再計算です。構成銘柄リストが現時点のものなので生存者バイアスが乗り、ブレッドス系は実際より良く出ます。また日次観測は期間が重なるため独立ではなく、1か月先の実質的な独立標本数は概ね「件数÷21」にとどまります。閾値の最適化には使わず、傾向の確認にとどめてください。"
        };
    }

    static ScoreBandValidation BuildScoreBandValidation(string label, List<HistoryEntry> entries)
    {
        var oneMonth = entries.Where(entry => entry.SpyReturn1m.HasValue && entry.QqqReturn1m.HasValue).ToList();
        var threeMonth = entries.Where(entry => entry.SpyReturn3m.HasValue && entry.QqqReturn3m.HasValue).ToList();
        return new ScoreBandValidation
        {
            Label = label,
            ObservationCount = entries.Count,
            OneMonthSampleSize = oneMonth.Count,
            ThreeMonthSampleSize = threeMonth.Count,
            SpyAverageReturn1m = AverageOrNull(oneMonth.Select(entry => entry.SpyReturn1m)),
            QqqAverageReturn1m = AverageOrNull(oneMonth.Select(entry => entry.QqqReturn1m)),
            SpyWinRate1m = WinRateOrNull(oneMonth.Select(entry => entry.SpyReturn1m)),
            QqqWinRate1m = WinRateOrNull(oneMonth.Select(entry => entry.QqqReturn1m)),
            SpyAverageMaxDrawdown1m = AverageOrNull(oneMonth.Select(entry => entry.SpyMaxDrawdown1m)),
            QqqAverageMaxDrawdown1m = AverageOrNull(oneMonth.Select(entry => entry.QqqMaxDrawdown1m)),
            SpyAverageReturn3m = AverageOrNull(threeMonth.Select(entry => entry.SpyReturn3m)),
            QqqAverageReturn3m = AverageOrNull(threeMonth.Select(entry => entry.QqqReturn3m)),
            SpyWinRate3m = WinRateOrNull(threeMonth.Select(entry => entry.SpyReturn3m)),
            QqqWinRate3m = WinRateOrNull(threeMonth.Select(entry => entry.QqqReturn3m)),
            SpyAverageMaxDrawdown3m = AverageOrNull(threeMonth.Select(entry => entry.SpyMaxDrawdown3m)),
            QqqAverageMaxDrawdown3m = AverageOrNull(threeMonth.Select(entry => entry.QqqMaxDrawdown3m))
        };
    }

    static decimal? AverageOrNull(IEnumerable<decimal?> values)
    {
        var numbers = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return numbers.Count == 0 ? null : Math.Round(numbers.Average(), 2);
    }

    static decimal? WinRateOrNull(IEnumerable<decimal?> returns)
    {
        var values = returns.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return values.Count == 0 ? null : Math.Round(values.Count(value => value > 0m) / (decimal)values.Count * 100m, 1);
    }

    static void PersistHistory(List<HistoryEntry> history)
    {
        if (history.Any(entry => !IsValidHistoryEntry(entry)))
            throw new InvalidDataException("history.json に不正なデータがあるため、市場リスクスコアを保存できません。");

        var trimmed = history.OrderBy(entry => entry.Date, StringComparer.Ordinal).TakeLast(HISTORY_MAX_ENTRIES).ToList();

        // 採点内訳（marketRiskMetrics）は「今日のスコア変動理由」でしか使わず、必要なのは直近数件だけ。
        // 1件あたり19項目×説明文で数KBになり、全件保持するとファイルが1MB近くまで膨らんで
        // 5分ごとの再取得が重くなるため、古いエントリからは落とす。
        for (int i = 0; i < trimmed.Count - HISTORY_METRICS_RETAINED; i++) trimmed[i].MarketRiskMetrics = null;

        WriteTextAtomically(GetOutputPath("history.json"), JsonSerializer.Serialize(trimmed, JsonOptions));
    }

    // ================= モデル =================

    // 分配金・株式分割を反映した終値と、実取引の出来高のみを保持する。
    record DailyData(DateTime Date, decimal AdjustedClose, long Volume);

    class IndexAnalysis
    {
        public string Name { get; set; } = "";
        public string DataAsOf { get; set; } = "";
        public decimal LatestAdjustedClose { get; set; }
        public decimal? Sma50 { get; set; }
        public bool IsAboveSma50 { get; set; }
        public decimal High52WeekAdjusted { get; set; }
        public decimal DrawdownFromHighPct { get; set; } // 0以下の値（52週高値からの下落率）
        public int DistributionDaysActive { get; set; }
        // アクティブな売り抜け日の Σ(下落率 × 出来高/50日平均出来高)。大きいほど売りが重い。
        public decimal DistributionIntensity { get; set; }
        public decimal? WorstActiveDropPct { get; set; } // アクティブな売り抜け日の中での最大下落率
        public string? WorstActiveDropDate { get; set; }
        public string TrendState { get; set; } = ""; // Correction / RallyAttempt / ConfirmedUptrend
        public string? LastFollowThroughDate { get; set; }
        public string StatusId { get; set; } = ""; // Uptrend / Pressure / Correction
        public ChartData Chart { get; set; } = new();
    }

    class ChartData
    {
        public List<string> Labels { get; set; } = new();
        public List<decimal> Prices { get; set; } = new();
        public List<decimal?> Sma50 { get; set; } = new();
        public List<decimal?> DistMarksActive { get; set; } = new();  // 現在も有効な売り抜け日
        public List<decimal?> DistMarksExpired { get; set; } = new(); // 25日経過/5%反発で失効した売り抜け日
    }

    class SectorInfo
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Return1m { get; set; }
        public decimal Return3m { get; set; }
        public decimal RelStrength1m { get; set; } // SPY比（1ヶ月）
        public decimal RelStrength3m { get; set; } // SPY比（3ヶ月）
    }

    // ---- 生データ（取得は1回、計算は基準日ごとに何度でも） ----
    class SectorRotationData
    {
        public List<DailyData> SpyData { get; set; } = new();
        public Dictionary<string, List<DailyData>> Sectors { get; set; } = new();
        public Dictionary<string, List<DailyData>> Proxies { get; set; } = new();
    }

    class CreditData
    {
        public List<DailyData> HygData { get; set; } = new();
        public List<DailyData> LqdData { get; set; } = new();
        public List<(DateTime Date, decimal Value)> HyOasSeries { get; set; } = new();
    }

    class VolatilityData
    {
        public List<DailyData> VixData { get; set; } = new();
        public List<DailyData>? Vix3mData { get; set; }
    }

    class BreadthData
    {
        public int ExpectedConstituents { get; set; }
        public Dictionary<string, List<DailyData>> Symbols { get; set; } = new();
    }

    // 全市場データをまとめて保持し、任意の基準日でスコアを再計算できるようにする。
    class MarketDataBundle
    {
        public List<DailyData> Sp500 { get; set; } = new();
        public List<DailyData> Nasdaq { get; set; } = new();
        public SectorRotationData? Sector { get; set; }
        public CreditData? Credit { get; set; }
        public VolatilityData? Volatility { get; set; }
        public BreadthData? Breadth { get; set; }
    }

    // ある基準日時点の市場評価一式。実運用日もバックフィル日も同じ関数で作るため、比較可能性が保たれる。
    class MarketSnapshot
    {
        public string MarketDataAsOf { get; set; } = "";
        public IndexAnalysis Sp500 { get; set; } = new();
        public IndexAnalysis Nasdaq { get; set; } = new();
        public string CombinedStatus { get; set; } = "";
        public string CombinedDrivenBy { get; set; } = "";
        public SectorRotationOutput Sector { get; set; } = new();
        public CreditRiskAppetiteOutput Credit { get; set; } = new();
        public VolatilityOutput Volatility { get; set; } = new();
        public MarketBreadthOutput Breadth { get; set; } = new();
        public PutCallOutput PutCall { get; set; } = new();
        public MarketRiskScore RiskScore { get; set; } = new();
    }

    class SectorRotationResult
    {
        public decimal SpyReturn1m { get; set; }
        public decimal SpyReturn3m { get; set; }
        public decimal? DefensiveReturn1m { get; set; }
        public decimal? CyclicalReturn1m { get; set; }
        // ディフェンシブ − シクリカル。プラスが大きいほど守りへの資金移動が速い。
        public decimal? RotationSpread1m { get; set; }
        public decimal? RotationSpread3m { get; set; }
        public List<SectorInfo> Sectors { get; set; } = new();
        public List<SectorInfo> BreadthProxies { get; set; } = new();
    }

    class SectorRotationOutput
    {
        public string Status { get; set; } = ""; // ok / unavailable
        public decimal? SpyReturn1m { get; set; }
        public decimal? SpyReturn3m { get; set; }
        public decimal? DefensiveReturn1m { get; set; }
        public decimal? CyclicalReturn1m { get; set; }
        public decimal? RotationSpread1m { get; set; }
        public decimal? RotationSpread3m { get; set; }
        public List<SectorInfo>? Sectors { get; set; }
        public List<SectorInfo>? BreadthProxies { get; set; }
        public string Note { get; set; } = "";
    }

    class CreditRiskAppetiteResult
    {
        public decimal HygReturn1m { get; set; }
        public decimal HygReturn3m { get; set; }
        public decimal LqdReturn1m { get; set; }
        public decimal LqdReturn3m { get; set; }
        public decimal Spread1m { get; set; } // HYGリターン - LQDリターン（1ヶ月）
        public decimal Spread3m { get; set; } // 同（3ヶ月）
        public decimal? HyOasPct { get; set; }
        public decimal? HyOasChange1mBps { get; set; }
        public string? HyOasDate { get; set; }
    }

    class CreditRiskAppetiteOutput
    {
        public string Status { get; set; } = ""; // ok / unavailable
        public decimal? HygReturn1m { get; set; }
        public decimal? HygReturn3m { get; set; }
        public decimal? LqdReturn1m { get; set; }
        public decimal? LqdReturn3m { get; set; }
        public decimal? Spread1m { get; set; }
        public decimal? Spread3m { get; set; }
        public decimal? HyOasPct { get; set; }
        public decimal? HyOasChange1mBps { get; set; }
        public string? HyOasDate { get; set; }
        public string Note { get; set; } = "";
    }

    class HyOasResult
    {
        public decimal ValuePct { get; set; }
        public decimal Change1mBps { get; set; }
        public string Date { get; set; } = "";
    }

    class VolatilityResult
    {
        public decimal Vix { get; set; }
        public decimal VixSma20 { get; set; }
        public decimal? Vix3m { get; set; }
        public decimal? RealizedVolShortPct { get; set; }
        public decimal? RealizedVolLongPct { get; set; }
        public decimal? RealizedVol21Pct { get; set; }
        public decimal? VarianceRiskPremium { get; set; }
        public decimal TermSlopePct { get; set; }
        public string TermStructure { get; set; } = "";
        public string TermSource { get; set; } = ""; // VIX3M / RealizedVol
    }

    class VolatilityOutput
    {
        public string Status { get; set; } = ""; // ok / partial / unavailable
        public decimal? Vix { get; set; }
        public decimal? VixSma20 { get; set; }
        public decimal? Vix3m { get; set; }
        public decimal? RealizedVolShortPct { get; set; }
        public decimal? RealizedVolLongPct { get; set; }
        public decimal? RealizedVol21Pct { get; set; }
        public decimal? VarianceRiskPremium { get; set; }
        public decimal? TermSlopePct { get; set; }
        public string? TermStructure { get; set; }
        public string? TermSource { get; set; }
        public string Note { get; set; } = "";
    }

    class MarketBreadthResult
    {
        public string UniverseAsOf { get; set; } = "";
        public int ExpectedConstituents { get; set; }
        public int AnalyzedConstituents { get; set; }
        public decimal CoveragePct { get; set; }
        public decimal? AboveSma50Pct { get; set; }
        public decimal? AboveSma200Pct { get; set; }
        public int NewHighs52Week { get; set; }
        public int NewLows52Week { get; set; }
        public int Advances { get; set; }
        public int Declines { get; set; }
        public int AdvanceDeclineNet { get; set; }
        public int AdLineChange20d { get; set; }
        // 銘柄間の平均ペア相関（21日）。高いほどマクロ一括・分散が効かない地合い。
        public decimal? AvgPairwiseCorrelation { get; set; }
        // 直近50日の出来高が上昇日に偏っている（＝機関が蓄積している）銘柄の比率。
        public decimal? AccumulationPct { get; set; }
        // 50日線上の銘柄のうち、出来高は下落日に偏っている銘柄の比率＝ステルス配分。
        public decimal? StealthDistributionPct { get; set; }
        // 10日騰落レシオ（上昇 / (上昇+下落)）の移動平均。
        public decimal? AdvanceRatioSma10 { get; set; }
        public bool BreadthThrustDetected { get; set; }
    }

    class MarketBreadthOutput : MarketBreadthResult
    {
        public string Status { get; set; } = ""; // ok / partial / unavailable
        public string Note { get; set; } = "";
    }

    class MarketRiskMetric
    {
        public string Group { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Score { get; set; }
        public decimal MaxPoints { get; set; }
        public string Detail { get; set; } = "";
    }

    class MarketRiskScore
    {
        public decimal Score { get; set; }
        public decimal RawScore { get; set; }
        public decimal AvailableMaxPoints { get; set; }
        public decimal DataCoveragePct { get; set; }
        public string Label { get; set; } = "";
        public List<MarketRiskMetric> Metrics { get; set; } = new();
    }

    class HistoryEntry
    {
        public string Date { get; set; } = "";
        // スコア計算時に使った市場の基準日と調整後終値。
        // 後日の検証ではこの値を起点にするため、古い履歴を推測で補完しない。
        public string? MarketDataAsOf { get; set; }
        // "live"（当日実行）か "backfill"（過去再計算）か。
        // バックフィル分はPut/Callが常に欠測で、構成銘柄リストの生存者バイアスも乗るため区別する。
        public string? Source { get; set; }
        // 採点体系の版。配点を変えた前後のスコアを同じ箱で集計しないための識別子。
        public int? RubricVersion { get; set; }
        public string CombinedStatus { get; set; } = "";
        public string Sp500Status { get; set; } = "";
        public int Sp500DistDays { get; set; }
        public string NasdaqStatus { get; set; } = "";
        public int NasdaqDistDays { get; set; }
        public decimal? PutCallRatio { get; set; }
        public decimal? MarketRiskScore { get; set; }
        public decimal? MarketRiskAvailableMaxPoints { get; set; }
        public List<MarketRiskMetric>? MarketRiskMetrics { get; set; }
        public decimal? SpyAdjustedClose { get; set; }
        public decimal? QqqAdjustedClose { get; set; }
        public decimal? SpyReturn1m { get; set; }
        public decimal? QqqReturn1m { get; set; }
        public decimal? SpyMaxDrawdown1m { get; set; }
        public decimal? QqqMaxDrawdown1m { get; set; }
        public decimal? SpyReturn3m { get; set; }
        public decimal? QqqReturn3m { get; set; }
        public decimal? SpyMaxDrawdown3m { get; set; }
        public decimal? QqqMaxDrawdown3m { get; set; }
    }

    class MarketRiskChange
    {
        public string Status { get; set; } = ""; // ok / collecting
        public string? PreviousDate { get; set; }
        public decimal? PreviousScore { get; set; }
        public decimal? ScoreChange { get; set; }
        public decimal? CoverageChange { get; set; }
        public List<MarketRiskChangeFactor> Factors { get; set; } = new();
        public string Note { get; set; } = "";
    }

    class MarketRiskChangeFactor
    {
        public string Group { get; set; } = "";
        public string Name { get; set; } = "";
        // 正なら市場リスクを押し上げ、負なら押し下げた寄与（100点換算）。
        public decimal ChangeInRiskPoints { get; set; }
        public string CurrentDetail { get; set; } = "";
        public string PreviousDetail { get; set; } = "";
    }

    class ScoreValidationOutput
    {
        public string Status { get; set; } = ""; // collecting / preliminary
        public int ObservationCount { get; set; }
        public int OneMonthMaturedCount { get; set; }
        public int ThreeMonthMaturedCount { get; set; }
        public int RecommendedMinSamples { get; set; }
        // うち過去データからの再計算分。生存者バイアスとPut/Call欠測を含むため、実運用分と区別して示す。
        public int BackfilledCount { get; set; }
        public List<ScoreBandValidation> Bands { get; set; } = new();
        public string Note { get; set; } = "";
    }

    class ScoreBandValidation
    {
        public string Label { get; set; } = "";
        public int ObservationCount { get; set; }
        public int OneMonthSampleSize { get; set; }
        public int ThreeMonthSampleSize { get; set; }
        public decimal? SpyAverageReturn1m { get; set; }
        public decimal? QqqAverageReturn1m { get; set; }
        public decimal? SpyWinRate1m { get; set; }
        public decimal? QqqWinRate1m { get; set; }
        public decimal? SpyAverageMaxDrawdown1m { get; set; }
        public decimal? QqqAverageMaxDrawdown1m { get; set; }
        public decimal? SpyAverageReturn3m { get; set; }
        public decimal? QqqAverageReturn3m { get; set; }
        public decimal? SpyWinRate3m { get; set; }
        public decimal? QqqWinRate3m { get; set; }
        public decimal? SpyAverageMaxDrawdown3m { get; set; }
        public decimal? QqqAverageMaxDrawdown3m { get; set; }
    }

    class PutCallResult
    {
        public string Underlying { get; set; } = "";
        public long CallVolume { get; set; }
        public long PutVolume { get; set; }
        public decimal Ratio { get; set; }
    }

    class HistoryPreparation
    {
        public List<HistoryEntry> Entries { get; set; } = new();
        public PutCallStats PutCallStats { get; set; } = new();
    }

    class PutCallStats
    {
        public decimal? Sma10 { get; set; }
        public double? PercentileRank { get; set; }
        public int HistoryDays { get; set; }
    }

    class PutCallOutput
    {
        public string Status { get; set; } = ""; // ok / unavailable
        public string Underlying { get; set; } = "";
        public long? CallVolume { get; set; }
        public long? PutVolume { get; set; }
        public decimal? Ratio { get; set; }
        public decimal? Sma10 { get; set; }
        public double? PercentileRank { get; set; }
        public int? HistoryDays { get; set; }
        public string Note { get; set; } = "";
    }

    class YahooResult { [JsonPropertyName("chart")] public Chart? Chart { get; set; } }
    class Chart { [JsonPropertyName("result")] public Result[]? Result { get; set; } }
    class Result
    {
        [JsonPropertyName("timestamp")] public long[]? Timestamp { get; set; }
        [JsonPropertyName("indicators")] public Indicators? Indicators { get; set; }
    }
    class Indicators
    {
        [JsonPropertyName("quote")] public Quote[]? Quote { get; set; }
        [JsonPropertyName("adjclose")] public AdjustedQuote[]? AdjClose { get; set; }
    }
    class Quote
    {
        [JsonPropertyName("close")] public decimal?[]? Close { get; set; }
        [JsonPropertyName("volume")] public long?[]? Volume { get; set; }
    }
    class AdjustedQuote { [JsonPropertyName("adjclose")] public decimal?[]? AdjustedClose { get; set; } }

    // ---- Yahoo Finance オプションチェーンAPI（Put/Call Ratio用） ----
    class YahooOptionsResult { [JsonPropertyName("optionChain")] public OptionChainRoot? OptionChain { get; set; } }
    class OptionChainRoot { [JsonPropertyName("result")] public OptionChainResult[]? Result { get; set; } }
    class OptionChainResult { [JsonPropertyName("options")] public OptionsGroup[]? Options { get; set; } }
    class OptionsGroup
    {
        [JsonPropertyName("calls")] public OptionContract[]? Calls { get; set; }
        [JsonPropertyName("puts")] public OptionContract[]? Puts { get; set; }
    }
    class OptionContract
    {
        [JsonPropertyName("volume")] public long? Volume { get; set; }
    }
}
