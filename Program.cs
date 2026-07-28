using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;

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

    // HttpClientは使い回す（毎回newすると接続のオーバーヘッドやソケット枯渇のリスクが積み重なるため、プロセス内で1つを共有する）
    static readonly HttpClient httpClient = CreateHttpClient();

    static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // User-Agentを設定しないとYahoo側から拒否される場合があるため追加
        c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        return c;
    }

    static async Task Main()
    {
        try
        {
            Console.WriteLine("Fetching index data from Yahoo Finance...");

            // IBD方式に合わせ、S&P500 と Nasdaq Composite の両方を見る（QQQ単体はNasdaq-100であり代替として不正確なため使用しない）
            var sp500Data = await FetchYahooDataWithRetry("%5EGSPC", "1y");
            var nasdaqData = await FetchYahooDataWithRetry("%5EIXIC", "1y");

            var sp500 = AnalyzeIndex("S&P 500", sp500Data);
            var nasdaq = AnalyzeIndex("Nasdaq Composite", nasdaqData);

            // 2指数のうち「悪い方（より弱気な方）」を採用するのがIBD Market Pulseの流儀
            // ※ 同順位（引き分け）のときに片方だけを「弱いから採用」と表示すると誤解を招くため、
            //    引き分けは明示的に分岐して扱う
            int Rank(string status) => status switch { "Uptrend" => 2, "Pressure" => 1, _ => 0 };
            int rankSp = Rank(sp500.StatusId);
            int rankNq = Rank(nasdaq.StatusId);
            string combinedStatus;
            string combinedDrivenBy;
            if (rankSp == rankNq)
            {
                combinedStatus = sp500.StatusId;
                combinedDrivenBy = "S&P 500・Nasdaq Compositeともに同水準";
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

            // Put/Call Ratio（自前算出）。失敗しても既存のExposure機能全体を止めないよう内部で例外を握りつぶす設計
            var putCall = await FetchPutCallRatio();

            // 履歴を先に更新し、そこから移動平均・パーセンタイル順位を算出する
            var putCallStats = AppendHistory(combinedStatus, sp500, nasdaq, putCall?.Ratio);
            Console.WriteLine("history.json has been updated.");

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
                    Note = "SPYオプション出来高から自前算出した代替指標です。IBD/CBOEの公式値とは母集団が異なるため直接比較できません。"
                };

            var output = new
            {
                lastUpdated = DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd HH:mm:ss"),
                combinedStatus,
                combinedDrivenBy,
                sp500,
                nasdaq,
                putCallRatio = putCallOutput
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(output, options);
            File.WriteAllText("data.json", json);
            Console.WriteLine("data.json has been generated successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error occurred: {ex.Message}");
            Environment.Exit(1); // GitHub Actions側に失敗を検知させる
        }
    }

    // ================= データ取得 =================

    static async Task<List<DailyData>> FetchYahooDataWithRetry(string symbol, string range, int maxAttempts = 3)
    {
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await FetchYahooData(symbol, range);
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

    static async Task<List<DailyData>> FetchYahooData(string symbol, string range)
    {
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?interval=1d&range={range}";

        var responseString = await httpClient.GetStringAsync(url);
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

        var dailyData = new List<DailyData>();
        for (int i = 0; i < timestamps.Length; i++)
        {
            if (i < closes.Length && i < volumes.Length && closes[i].HasValue && volumes[i].HasValue)
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).DateTime;
                dailyData.Add(new DailyData(date, closes[i]!.Value, volumes[i]!.Value));
            }
        }

        var ordered = dailyData.OrderBy(x => x.Date).ToList();
        if (ordered.Count < 100)
        {
            throw new Exception($"計算に必要な100日分のデータが不足しています ({symbol})。");
        }
        return ordered;
    }

    // ================= Put/Call Ratio（自前算出） =================

    static async Task<PutCallResult?> FetchPutCallRatio()
    {
        // 設計方針：
        // ・呼び出し回数を1回に絞る（複数限月を合算する方式より壊れにくさを優先）
        // ・SPYは週次/デイリー(0DTE)オプションの出来高が非常に大きいため、
        //   直近限月だけでもその日の出来高のかなりの部分を捉えられる実用上の近似として採用
        // ・失敗しても例外を外に投げず null を返す＝Stock Market Exposure機能を道連れにしない
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var url = $"https://query1.finance.yahoo.com/v7/finance/options/{PUT_CALL_UNDERLYING}";
                var responseString = await httpClient.GetStringAsync(url);
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
            for (int i = 0; i < 50; i++) windowSum += data[i].Close;
            sma50[49] = Math.Round(windowSum / 50m, 2);
            for (int i = 50; i < n; i++)
            {
                windowSum += data[i].Close - data[i - 50].Close;
                sma50[i] = Math.Round(windowSum / 50m, 2);
            }
        }

        // --- 売り抜け日の「生」判定を先に1回だけ計算 ---
        // チャート表示用マーカーとアクティブ集計の両方でこの結果を使い回すことで、
        // 判定式が2箇所に重複してどちらか一方だけ修正され矛盾する事故を防ぐ
        var isRawDistDay = new bool[n];
        for (int i = 1; i < n; i++)
        {
            decimal dropPct = (data[i - 1].Close - data[i].Close) / data[i - 1].Close * 100m;
            isRawDistDay[i] = dropPct >= DIST_DAY_DROP_PCT && data[i].Volume > data[i - 1].Volume;
        }

        // --- 売り抜け日（失効ルール込みでアクティブな件数を日次で追跡） ---
        var distDaysActive = new int[n];
        var activeDDs = new List<(int idx, decimal close)>();
        for (int i = 1; i < n; i++)
        {
            // 25営業日経過 または そのDDの終値から5%以上反発 したものは無効化
            activeDDs.RemoveAll(dd => (i - dd.idx) > DIST_DAY_WINDOW || data[i].Close >= dd.close * (1 + DIST_DAY_INVALIDATE_RALLY_PCT / 100m));

            if (isRawDistDay[i])
            {
                activeDDs.Add((i, data[i].Close));
            }
            distDaysActive[i] = activeDDs.Count;
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
                bool aboveSma = data[i].Close >= sma50[i]!.Value;
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
                    decimal recentLow = data[lookback].Close;
                    for (int k = lookback + 1; k < i; k++)
                    {
                        if (data[k].Close < recentLow) recentLow = data[k].Close;
                    }
                    if (data[i].Close > data[i - 1].Close && data[i - 1].Close <= recentLow)
                    {
                        day1Index = i;
                        day1Low = data[i - 1].Close;
                        currentState = "RallyAttempt";
                    }
                }
                else
                {
                    if (data[i].Close < day1Low!.Value)
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
                            decimal gainPct = (data[i].Close - data[i - 1].Close) / data[i - 1].Close * 100m;
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

        bool isAboveSma50 = sma50[n - 1].HasValue && data[n - 1].Close >= sma50[n - 1]!.Value;

        // --- チャート表示用（直近100日） ---
        int chartStart = Math.Max(0, n - 100);
        var chartLabels = new List<string>();
        var chartCloses = new List<decimal>();
        var chartSma50 = new List<decimal?>();
        var distMarks = new List<decimal?>();

        for (int i = chartStart; i < n; i++)
        {
            chartLabels.Add(data[i].Date.ToString("MM-dd"));
            chartCloses.Add(data[i].Close);
            chartSma50.Add(sma50[i]);
            distMarks.Add(isRawDistDay[i] ? data[i].Close : (decimal?)null);
        }

        return new IndexAnalysis
        {
            Name = name,
            LatestClose = data[n - 1].Close,
            Sma50 = sma50[n - 1],
            IsAboveSma50 = isAboveSma50,
            DistributionDaysActive = lastDistDays,
            TrendState = lastState,
            LastFollowThroughDate = lastFtdDate?.ToString("yyyy-MM-dd"),
            StatusId = statusId,
            Chart = new ChartData
            {
                Labels = chartLabels,
                Closes = chartCloses,
                Sma50 = chartSma50,
                DistMarks = distMarks
            }
        };
    }

    // ================= 履歴保存 =================

    static PutCallStats AppendHistory(string combinedStatus, IndexAnalysis sp500, IndexAnalysis nasdaq, decimal? putCallRatio)
    {
        const string historyPath = "history.json";
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        List<HistoryEntry> history = new();
        if (File.Exists(historyPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(historyPath));
                if (existing != null) history = existing;
            }
            catch
            {
                // 壊れていた場合は履歴を作り直す
                history = new();
            }
        }

        string today = DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd");
        history.RemoveAll(h => h.Date == today); // 同日再実行時は上書き

        history.Add(new HistoryEntry
        {
            Date = today,
            CombinedStatus = combinedStatus,
            Sp500Status = sp500.StatusId,
            Sp500DistDays = sp500.DistributionDaysActive,
            NasdaqStatus = nasdaq.StatusId,
            NasdaqDistDays = nasdaq.DistributionDaysActive,
            PutCallRatio = putCallRatio
        });

        // 直近180日分のみ保持
        var trimmed = history.OrderBy(h => h.Date).TakeLast(180).ToList();
        File.WriteAllText(historyPath, JsonSerializer.Serialize(trimmed, options));

        // --- Put/Call Ratioのトレンド統計（保存後の履歴から算出。今日の値自身も含めて計算する） ---
        var validRatios = trimmed.Where(h => h.PutCallRatio.HasValue).Select(h => h.PutCallRatio!.Value).ToList();

        decimal? sma = validRatios.Count > 0
            ? Math.Round(validRatios.TakeLast(PUT_CALL_SMA_WINDOW).Average(), 3)
            : null;

        double? percentile = null;
        if (putCallRatio.HasValue && validRatios.Count >= PUT_CALL_MIN_HISTORY_FOR_PERCENTILE)
        {
            // 「自分以下の値が全体の何%を占めるか」＝高いほどプット優勢（弱気/ヘッジ需要が強い）な極値に近い
            int countAtOrBelow = validRatios.Count(v => v <= putCallRatio.Value);
            percentile = Math.Round((double)countAtOrBelow / validRatios.Count * 100.0, 1);
        }

        return new PutCallStats { Sma10 = sma, PercentileRank = percentile, HistoryDays = validRatios.Count };
    }

    // ================= モデル =================

    record DailyData(DateTime Date, decimal Close, long Volume);

    class IndexAnalysis
    {
        public string Name { get; set; } = "";
        public decimal LatestClose { get; set; }
        public decimal? Sma50 { get; set; }
        public bool IsAboveSma50 { get; set; }
        public int DistributionDaysActive { get; set; }
        public string TrendState { get; set; } = ""; // Correction / RallyAttempt / ConfirmedUptrend
        public string? LastFollowThroughDate { get; set; }
        public string StatusId { get; set; } = ""; // Uptrend / Pressure / Correction
        public ChartData Chart { get; set; } = new();
    }

    class ChartData
    {
        public List<string> Labels { get; set; } = new();
        public List<decimal> Closes { get; set; } = new();
        public List<decimal?> Sma50 { get; set; } = new();
        public List<decimal?> DistMarks { get; set; } = new();
    }

    class HistoryEntry
    {
        public string Date { get; set; } = "";
        public string CombinedStatus { get; set; } = "";
        public string Sp500Status { get; set; } = "";
        public int Sp500DistDays { get; set; }
        public string NasdaqStatus { get; set; } = "";
        public int NasdaqDistDays { get; set; }
        public decimal? PutCallRatio { get; set; }
    }

    class PutCallResult
    {
        public string Underlying { get; set; } = "";
        public long CallVolume { get; set; }
        public long PutVolume { get; set; }
        public decimal Ratio { get; set; }
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
    class Indicators { [JsonPropertyName("quote")] public Quote[]? Quote { get; set; } }
    class Quote
    {
        [JsonPropertyName("close")] public decimal?[]? Close { get; set; }
        [JsonPropertyName("volume")] public long?[]? Volume { get; set; }
    }

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

