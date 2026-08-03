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

    // ===== 値幅代理指標（Breadth Proxies） =====
    // 500銘柄の個別スキャンをせずに「上昇が広いか一部の大型株に偏っているか」を近似する代用指標。
    // RSP: S&P500均等加重（時価総額加重のSPYとの差が集中度の目安）
    // IWM: 小型株(Russell 2000)。景気敏感・高ベータでリスク選好の代理指標になる
    static readonly Dictionary<string, string> BREADTH_PROXY_ETFS = new()
    {
        ["RSP"] = "S&P500均等加重",
        ["IWM"] = "小型株(Russell 2000)"
    };

    // ===== Credit Risk Appetite（クレジット市場のリスク選好度） =====
    // HYG(ハイイールド社債ETF) と TLT(米国長期国債ETF) の相対リターン。
    // HYGがTLTに対して優勢＝クレジット市場がリスクオン、逆＝質への逃避（リスクオフ）
    const string CREDIT_RISK_ON_SYMBOL = "HYG";
    const string CREDIT_RISK_OFF_SYMBOL = "TLT";

    // HttpClientは使い回す（毎回newすると接続のオーバーヘッドやソケット枯渇のリスクが積み重なるため、プロセス内で1つを共有する）
    static readonly HttpClient httpClient = CreateHttpClient();

    static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // 単純な"Mozilla/5.0"だけだと逆に不自然でYahoo側にブロックされやすいため、実際のブラウザに近い文字列に強化
        c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
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

            // セクターローテーション（自前算出）。これも失敗して良い補助機能として独立させる
            var sectorRotation = await FetchSectorRotation();
            var sectorOutput = sectorRotation == null
                ? new SectorRotationOutput
                {
                    Status = "unavailable",
                    Note = "セクターデータの取得に失敗しました。詳細はActionsのログ（SectorRotationの行）を確認してください。"
                }
                : new SectorRotationOutput
                {
                    Status = "ok",
                    SpyReturn1m = sectorRotation.SpyReturn1m,
                    SpyReturn3m = sectorRotation.SpyReturn3m,
                    Sectors = sectorRotation.Sectors,
                    BreadthProxies = sectorRotation.BreadthProxies,
                    Note = "SPDRセクターETF11銘柄のSPYに対する相対リターン(自前算出)。CAN SLIMの「L(Leader)」に対応する補助指標です。"
                };

            // Credit Risk Appetite（自前算出）。これも失敗して良い補助機能として独立させる
            var creditRisk = await FetchCreditRiskAppetite();
            var creditRiskOutput = creditRisk == null
                ? new CreditRiskAppetiteOutput
                {
                    Status = "unavailable",
                    Note = "HYG/TLTデータの取得に失敗しました。詳細はActionsのログ（CreditRiskAppetiteの行）を確認してください。"
                }
                : new CreditRiskAppetiteOutput
                {
                    Status = "ok",
                    HygReturn1m = creditRisk.HygReturn1m,
                    HygReturn3m = creditRisk.HygReturn3m,
                    TltReturn1m = creditRisk.TltReturn1m,
                    TltReturn3m = creditRisk.TltReturn3m,
                    Spread1m = creditRisk.Spread1m,
                    Spread3m = creditRisk.Spread3m,
                    Note = "ハイイールド社債ETF(HYG)と長期国債ETF(TLT)の相対リターン(自前算出)。プラスが大きいほどクレジット市場がリスクオン、マイナスが大きいほど質への逃避（リスクオフ）を示唆します。"
                };

            var output = new
            {
                lastUpdated = DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd HH:mm:ss"),
                combinedStatus,
                combinedDrivenBy,
                sp500,
                nasdaq,
                putCallRatio = putCallOutput,
                sectorRotation = sectorOutput,
                creditRiskAppetite = creditRiskOutput
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
            var crumb = await crumbResponse.Content.ReadAsStringAsync();

            if (!crumbResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(crumb) || crumb.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[PutCallRatio] crumb取得に失敗しました（status={(int)crumbResponse.StatusCode}）。");
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

                var responseString = await response.Content.ReadAsStringAsync();
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

    static async Task<SectorRotationResult?> FetchSectorRotation()
    {
        try
        {
            // 基準となるSPY自身のリターンを先に取得
            var spyData = await FetchYahooDataWithRetry("SPY", "6mo");
            decimal spyReturn1m = ComputeReturnPct(spyData, SECTOR_RETURN_1M_DAYS);
            decimal spyReturn3m = ComputeReturnPct(spyData, SECTOR_RETURN_3M_DAYS);

            // 1銘柄取得してSPY比の相対強度を計算する共通処理（セクターと値幅代理指標の両方で使う）
            async Task<SectorInfo?> FetchOne(string symbol, string name)
            {
                try
                {
                    var data = await FetchYahooDataWithRetry(symbol, "6mo");
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
                catch (Exception ex)
                {
                    // 1銘柄の失敗は他の銘柄の表示を止める理由にしない
                    Console.WriteLine($"[SectorRotation] {symbol} の取得に失敗（この銘柄のみスキップ）: {ex.Message}");
                    return null;
                }
            }

            var sectorList = new List<SectorInfo>();
            foreach (var (symbol, name) in SECTOR_ETFS)
            {
                var info = await FetchOne(symbol, name);
                if (info != null) sectorList.Add(info);
                // Yahoo側への負荷を抑えるため、連続リクエストの間に軽くウェイトを入れる
                await Task.Delay(300);
            }

            // 値幅代理指標（RSP: 均等加重、IWM: 小型株）。500銘柄の個別スキャンをせずに
            // 「上昇が広いか一部の大型株に偏っているか」を近似する。こちらは失敗しても
            // セクターローテーション自体は成立させたいので、空でも先に進む
            var breadthList = new List<SectorInfo>();
            foreach (var (symbol, name) in BREADTH_PROXY_ETFS)
            {
                var info = await FetchOne(symbol, name);
                if (info != null) breadthList.Add(info);
                await Task.Delay(300);
            }

            if (sectorList.Count == 0)
            {
                Console.WriteLine("[SectorRotation] 全セクターの取得に失敗しました。");
                return null;
            }

            return new SectorRotationResult
            {
                SpyReturn1m = spyReturn1m,
                SpyReturn3m = spyReturn3m,
                // 3ヶ月の相対強度が高い順（＝リーダーシップが強い順）に並べる
                Sectors = sectorList.OrderByDescending(s => s.RelStrength3m).ToList(),
                BreadthProxies = breadthList
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SectorRotation] fetch failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    static decimal ComputeReturnPct(List<DailyData> data, int lookbackDays)
    {
        int n = data.Count;
        int startIdx = Math.Max(0, n - 1 - lookbackDays);
        decimal startClose = data[startIdx].Close;
        decimal endClose = data[n - 1].Close;
        return Math.Round((endClose - startClose) / startClose * 100m, 2);
    }

    // ================= Credit Risk Appetite（自前算出） =================

    static async Task<CreditRiskAppetiteResult?> FetchCreditRiskAppetite()
    {
        // HYGとTLTの「差」自体が指標の本体なので、片方だけ成功しても意味がない。
        // そのためセクターローテーションのような1銘柄ずつの部分成功は許容せず、
        // どちらかが失敗したら全体をunavailable扱いにする
        try
        {
            var hygData = await FetchYahooDataWithRetry(CREDIT_RISK_ON_SYMBOL, "6mo");
            await Task.Delay(300); // Yahoo側への負荷を抑える
            var tltData = await FetchYahooDataWithRetry(CREDIT_RISK_OFF_SYMBOL, "6mo");

            decimal hyg1m = ComputeReturnPct(hygData, SECTOR_RETURN_1M_DAYS);
            decimal hyg3m = ComputeReturnPct(hygData, SECTOR_RETURN_3M_DAYS);
            decimal tlt1m = ComputeReturnPct(tltData, SECTOR_RETURN_1M_DAYS);
            decimal tlt3m = ComputeReturnPct(tltData, SECTOR_RETURN_3M_DAYS);

            return new CreditRiskAppetiteResult
            {
                HygReturn1m = hyg1m,
                HygReturn3m = hyg3m,
                TltReturn1m = tlt1m,
                TltReturn3m = tlt3m,
                Spread1m = Math.Round(hyg1m - tlt1m, 2),
                Spread3m = Math.Round(hyg3m - tlt3m, 2)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreditRiskAppetite] fetch failed (non-fatal): {ex.Message}");
            return null;
        }
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

        // ループ終了時点のactiveDDsが「最新日時点でアクティブな売り抜け日」そのもの。
        // チャートでの有効/失効の視覚区別と、深刻さ（最大下落率）の算出に使う
        var activeDDIndices = activeDDs.Select(dd => dd.idx).ToHashSet();

        decimal? worstActiveDropPct = null;
        string? worstActiveDropDate = null;
        if (activeDDs.Count > 0)
        {
            int worstIdx = activeDDs
                .Select(dd => dd.idx)
                .OrderByDescending(idx => (data[idx - 1].Close - data[idx].Close) / data[idx - 1].Close)
                .First();
            worstActiveDropPct = Math.Round((data[worstIdx - 1].Close - data[worstIdx].Close) / data[worstIdx - 1].Close * 100m, 2);
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

        // --- 52週高値からのドローダウン（取得済みの1年分データからそのまま算出、追加取得コスト無し） ---
        decimal high52Week = data.Max(d => d.Close);
        decimal drawdownFromHighPct = Math.Round((data[n - 1].Close - high52Week) / high52Week * 100m, 2);

        // --- チャート表示用（直近100日） ---
        int chartStart = Math.Max(0, n - 100);
        var chartLabels = new List<string>();
        var chartCloses = new List<decimal>();
        var chartSma50 = new List<decimal?>();
        var distMarksActive = new List<decimal?>();
        var distMarksExpired = new List<decimal?>();

        for (int i = chartStart; i < n; i++)
        {
            chartLabels.Add(data[i].Date.ToString("MM-dd"));
            chartCloses.Add(data[i].Close);
            chartSma50.Add(sma50[i]);
            // 同じ「売り抜け日」でも、最新日時点でまだアクティブなものと、
            // 25日経過または5%反発で既に失効したものを別データセットとして分ける
            bool isActive = isRawDistDay[i] && activeDDIndices.Contains(i);
            bool isExpired = isRawDistDay[i] && !activeDDIndices.Contains(i);
            distMarksActive.Add(isActive ? data[i].Close : (decimal?)null);
            distMarksExpired.Add(isExpired ? data[i].Close : (decimal?)null);
        }

        return new IndexAnalysis
        {
            Name = name,
            LatestClose = data[n - 1].Close,
            Sma50 = sma50[n - 1],
            IsAboveSma50 = isAboveSma50,
            High52Week = high52Week,
            DrawdownFromHighPct = drawdownFromHighPct,
            DistributionDaysActive = lastDistDays,
            WorstActiveDropPct = worstActiveDropPct,
            WorstActiveDropDate = worstActiveDropDate,
            TrendState = lastState,
            LastFollowThroughDate = lastFtdDate?.ToString("yyyy-MM-dd"),
            StatusId = statusId,
            Chart = new ChartData
            {
                Labels = chartLabels,
                Closes = chartCloses,
                Sma50 = chartSma50,
                DistMarksActive = distMarksActive,
                DistMarksExpired = distMarksExpired
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
        public decimal High52Week { get; set; }
        public decimal DrawdownFromHighPct { get; set; } // 0以下の値（52週高値からの下落率）
        public int DistributionDaysActive { get; set; }
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
        public List<decimal> Closes { get; set; } = new();
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

    class SectorRotationResult
    {
        public decimal SpyReturn1m { get; set; }
        public decimal SpyReturn3m { get; set; }
        public List<SectorInfo> Sectors { get; set; } = new();
        public List<SectorInfo> BreadthProxies { get; set; } = new();
    }

    class SectorRotationOutput
    {
        public string Status { get; set; } = ""; // ok / unavailable
        public decimal? SpyReturn1m { get; set; }
        public decimal? SpyReturn3m { get; set; }
        public List<SectorInfo>? Sectors { get; set; }
        public List<SectorInfo>? BreadthProxies { get; set; }
        public string Note { get; set; } = "";
    }

    class CreditRiskAppetiteResult
    {
        public decimal HygReturn1m { get; set; }
        public decimal HygReturn3m { get; set; }
        public decimal TltReturn1m { get; set; }
        public decimal TltReturn3m { get; set; }
        public decimal Spread1m { get; set; } // HYGリターン - TLTリターン（1ヶ月）
        public decimal Spread3m { get; set; } // 同（3ヶ月）
    }

    class CreditRiskAppetiteOutput
    {
        public string Status { get; set; } = ""; // ok / unavailable
        public decimal? HygReturn1m { get; set; }
        public decimal? HygReturn3m { get; set; }
        public decimal? TltReturn1m { get; set; }
        public decimal? TltReturn3m { get; set; }
        public decimal? Spread1m { get; set; }
        public decimal? Spread3m { get; set; }
        public string Note { get; set; } = "";
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
