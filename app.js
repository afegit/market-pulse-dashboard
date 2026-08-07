const statusMeta = {
  Correction: { en: 'Market in Correction', jp: '調整局面', tone: 'risk' },
  Pressure: { en: 'Uptrend Under Pressure', jp: '上昇トレンドだが警戒', tone: 'warn' },
  Uptrend: { en: 'Confirmed Uptrend', jp: '上昇トレンドを確認', tone: 'good' }
};
const trendLabel = { Correction: '調整 / 未確認', RallyAttempt: '反発を試す段階', ConfirmedUptrend: '上昇トレンド確認済み' };
const charts = new Map();
let lastLoadedAt = 0;

// 用語の説明辞書。キーは表示ラベルから空白を除いたもの（normalizeTerm参照）。
// 初心者にも伝わるよう、専門用語はできるだけ言い換えて説明する。
const glossary = {
  '市場トレンド': '株価が今どちらの方向を向いているかを示す分類です。上昇・警戒・調整のどれに近いかをまとめています。',
  '市場ブレッドス': '指数（SPYやQQQ）だけでなく、個別銘柄がどれだけ幅広く上昇・下落しているかを示す分類です。指数が上がっていても多くの銘柄が弱いと危険信号になります。',
  '機関需給': '出来高（売買された株数）の偏りから、大口投資家（機関投資家）が買っているか売っているかを推測する分類です。',
  '市場構造': '銘柄同士が同じ方向に動きやすいか、それぞれ別の理由でバラバラに動きやすいかを示す分類です。',
  'ボラティリティ': '株価の変動の激しさです。高いほど値動きが荒く、市場が不安定になっているサインとされます。',
  'クレジット': '社債（企業の借金）市場の動きから、投資家がどれだけリスクを取りたがっているかを見る分類です。',
  'セクター': '業種別の株価グループ（テクノロジー、金融など）の強弱を見る分類です。',
  '需給': 'オプション取引（株を将来売買する権利の取引）の状況から、投資家心理の偏りを見る分類です。',

  '市場ステータス': 'SPYとQQQが「上昇トレンド」「上昇トレンドだが警戒」「調整局面」のどれに該当するかの判定です。両方のうち悪い方の判定が採用されます。',
  '50日線': '直近50営業日の株価の平均値（50日移動平均線）です。株価がこの線より上にあると、短中期的に上向きの流れが続いているとされます。',
  '有効DistributionDay': '「売り抜け日」と呼ばれる、機関投資家が売っていると見られる日のうち、直近25営業日以内でまだ有効な件数です。多いほど下落圧力が強いサインです。',
  '売り抜け強度': '売り抜け日それぞれの下落率と出来高の大きさを掛け合わせて合計した値です。件数だけでなく、売りの「本気度」の大きさを測ります。',
  '50日線上比率': '構成銘柄のうち、株価が50日移動平均線より上にある銘柄の割合です。高いほど多くの銘柄が上昇基調にあります。',
  '200日線上比率': '構成銘柄のうち、株価が200日移動平均線（長期トレンドの目安）より上にある銘柄の割合です。',
  '52週新高値・新安値': '直近52週間（約1年）で最高値・最安値を更新した銘柄の数です。新高値の方が多いほど強い相場とされます。',
  '20日A/Dライン': '直近20営業日について「上昇した銘柄数−下落した銘柄数」を毎日積み上げた合計値です。プラスが続くほど値上がりが幅広い銘柄に広がっています。',
  '10日騰落レシオ': '上昇した銘柄の割合（上昇銘柄数 ÷ (上昇銘柄数+下落銘柄数)）の10日平均です。0.5を大きく下回る状態が続くと、下落銘柄が優勢な地合いが長引いていることを示します。',
  'アキュムレーション銘柄比率': '株価が上がった日の出来高が、下がった日の出来高を上回っている銘柄の割合です。高いほど「買い」が優勢な銘柄が多いことを示します。',
  'ステルス配分': '株価は50日移動平均線より上（一見好調）なのに、出来高は下落した日の方が多い銘柄の割合です。チャートの値上がりだけを見ていると気づきにくい、隠れた売り圧力のサインです。',
  '銘柄間相関': '銘柄同士の値動きの連動度合いです。高いほど「個別の材料に関係なく、みんな同じ方向に動く」相場で、銘柄選びや分散投資の効果が薄れます。',
  'VIX対20日平均': '「恐怖指数」と呼ばれるVIX（株価の変動予想の大きさ）が、直近20日間の平均より高いか低いかです。高いほど市場が警戒しています。',
  'VIX期限構造': '短期（VIX）と3か月先（VIX3M）、どちらの変動予想が大きいかの関係です。通常は先の方が高く出ますが、逆転すると強い警戒信号とされます。',
  '分散リスクプレミアム': '事前に予想されていた変動の大きさ（VIX）と、実際に起きた変動の大きさ（実現ボラティリティ）の差です。マイナスは「市場の警戒が現実の値動きに追いついていない」状態を示します。',
  'HY対IG相対リターン': '信用力の低い社債（ハイイールド債）と、信用力の高い社債（投資適格債）の値動きの差です。ハイイールド債の方が弱いと、投資家がリスクを避け始めているサインです。',
  'HYOAS水準': 'ハイイールド債（信用力の低い社債）の、国債に対する上乗せ金利（信用スプレッド）の水準です。高いほど社債市場が警戒しています。',
  'HYOAS1か月変化': '上記の信用スプレッドが、1か月でどれだけ変化したかです。急拡大は信用不安の高まりを示します。',
  '対SPYで優位なセクター数': '市場平均（SPY）より値動きが強い業種セクターの数です。少ないほど、上昇が一部の業種に偏っています。',
  'RSP・IWMの相対強度': '時価総額に関係なく全銘柄を均等に扱う指数（RSP）と、小型株の指数（IWM）が、SPYと比べてどれだけ強いか弱いかです。',
  'ディフェンシブ優位度': '生活必需品・公益・ヘルスケアなど「守りのセクター」が、消費財・テック・資本財など「攻めのセクター」よりどれだけ強いかです。プラスが大きいほど、資金が守りに回っています。',
  'Put/Call極端値': '弱気に賭けるPutオプションと、強気に賭けるCallオプションの出来高比率が、過去の記録と比べて極端な水準にあるかどうかです。',

  'データ基準日': 'この画面のデータが計算された、米国市場の取引日です。',
  '調整後終値': '配当や株式分割の影響を調整した終値です。長期の値動きを正しく比較するために使います。',
  '50日移動平均': '直近50営業日の終値の平均値です。短中期の値動きの流れを見る目安になります。',
  '52週高値からの下落': '直近52週間（約1年）の最高値から、現在どれだけ下落しているかです。',
  'トレンド': '株価が「調整局面」「反発を試す段階」「上昇トレンド確認済み」のどこにあるかの判定です。',
  '直近FTD': 'フォロースルーデー（Follow-Through Day）の略。下落相場からの反発が本物である可能性が高いことを示す、出来高を伴った大幅上昇日です。',
  '最大の売り圧力': '直近の売り抜け日の中で、最も下落率が大きかった日の下落幅です。',
  '50日線上': '構成銘柄のうち、株価が50日移動平均線より上にある銘柄の割合です。',
  '200日線上': '構成銘柄のうち、株価が200日移動平均線（長期トレンドの目安）より上にある銘柄の割合です。',
  '52週新高値/新安値': '直近52週間で最高値・最安値を更新した銘柄数です。',
  '20日A/D変化': '直近20営業日の「上昇銘柄数−下落銘柄数」の積み上げです。プラスが続くほど値上がりの広がりが強いことを示します。',
  'VIX3M': '3か月先を対象にした、株価の変動予想の大きさ（インプライド・ボラティリティ）です。',
  'SPY実現ボラ': 'SPYで実際に起きた値動きの大きさ（実現ボラティリティ）を年率換算した値です。予想値であるVIXと違い、実際の結果に基づきます。',
  '短期ボラの優勢度': '短期の実現ボラティリティが、長期の実現ボラティリティをどれだけ上回っているかです。VIX3Mが取得できないときの代替指標として使います。',
  '期限構造': '短期と長期、どちらの変動予想が大きいかの関係です。',
  '短期対長期': '短期の変動の大きさと、長期の変動の大きさを比べた差です。',
  'VIX対VIX3M': '現在のVIXと、3か月先の予想変動率（VIX3M）を比べた差です。',
  'HYG3か月': 'ハイイールド債（信用力の低い社債）ETFであるHYGの、直近3か月のリターンです。',
  'LQD3か月': '投資適格社債（信用力の高い社債）ETFであるLQDの、直近3か月のリターンです。',
  'HY対IG': 'ハイイールド債と投資適格債の、3か月リターンの差です。',
  'HYOAS': 'ハイイールド債の、国債に対する上乗せ金利（信用スプレッド）です。',
  'アキュムレーション比率': '直近50日について、株価が上がった日の出来高が下がった日の出来高を上回っている銘柄の割合です。',
  'SPY売り抜け強度': 'SPYの売り抜け日について、下落率と出来高の大きさを掛け合わせて合計した値です。',
  'QQQ売り抜け強度': 'QQQの売り抜け日について、下落率と出来高の大きさを掛け合わせて合計した値です。',
  '銘柄間平均ペア相関': '構成銘柄どうしの値動きの連動度合いの平均です。高いほど、個別の材料に関係なく全体が同じ方向に動く相場です。',
  'BreadthThrust': '弱気相場から強気相場への転換を示す、歴史的に的中率が高いとされるシグナルです。10日騰落レシオが短期間で急速に改善すると点灯します。',
  'Put/Call': 'Putオプション（弱気に賭ける取引）の出来高を、Callオプション（強気に賭ける取引）の出来高で割った比率です。高いほど弱気（Put優勢）です。',
  '10日平均': 'Put/Call比率の直近10日間の平均値です。日々のブレを均して短期のトレンドを見ます。',
  '履歴順位': '今日のPut/Call比率が、これまでの記録の中で高い方から数えて何パーセントに位置するかです。',
  '出来高': 'その日に売買されたオプションの枚数です。',
  'スコア記録': '市場リスクスコアが記録された日数です。',
  '1か月後の確定実績': 'そのスコア帯を記録してから1か月（21営業日）後に、実際にSPY・QQQがどう動いたかの実績データです。',
  '3か月後の確定実績': 'そのスコア帯を記録してから3か月（63営業日）後に、実際にSPY・QQQがどう動いたかの実績データです。',
  'S&P500均等加重(RSP)': '時価総額の大小に関係なく、S&P500の全銘柄を均等な比率で保有する指数です。大型株だけに支えられた相場でないかを確認する目安になります。',
  '小型株(Russell2000)(IWM)': '小型株中心の指数（Russell 2000）です。景気に敏感でリスク選好度合いの目安になります。'
};

const normalizeTerm = text => String(text ?? '').replace(/\s+/g, '');

const byId = id => document.getElementById(id);
const number = (value, digits = 2) => value == null || Number.isNaN(Number(value)) ? '--' : Number(value).toFixed(digits);
const pct = (value, digits = 2) => value == null || Number.isNaN(Number(value)) ? '--' : `${Number(value).toFixed(digits)}%`;
const signedPct = (value, digits = 2) => value == null || Number.isNaN(Number(value)) ? '--' : `${Number(value) > 0 ? '+' : ''}${Number(value).toFixed(digits)}%`;
const signed = value => Number(value) > 0 ? `+${value}` : String(value);
const toneClass = tone => `tone-${tone || 'muted'}`;
const isAvailable = block => block && (block.status === 'ok' || block.status === 'partial');

function setPill(id, text, tone = 'muted') {
  const element = byId(id);
  element.className = `pill ${toneClass(tone)}`;
  element.textContent = text;
}

function createNode(tag, className, content) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (content != null) node.textContent = content;
  return node;
}

// createNodeと同じだが、glossaryに一致する用語には点線下線とツールチップを付与する。
function termNode(tag, className, content) {
  const node = createNode(tag, className, content);
  const tip = glossary[normalizeTerm(content)];
  if (tip) {
    node.classList.add('term');
    node.setAttribute('data-tip', tip);
    node.setAttribute('tabindex', '0');
  }
  return node;
}

function replaceMetrics(id, rows) {
  const container = byId(id);
  const fragment = document.createDocumentFragment();
  rows.forEach(([label, value, tone]) => {
    const row = createNode('div', 'metric');
    row.append(termNode('span', '', label));
    const strong = createNode('strong', tone ? toneClass(tone) : '', value);
    row.append(strong); fragment.append(row);
  });
  container.replaceChildren(fragment);
}

function replaceDataGrid(id, items) {
  const container = byId(id);
  const fragment = document.createDocumentFragment();
  items.forEach(({ label, value, sub, tone }) => {
    const card = createNode('div', 'datum');
    card.append(termNode('span', '', label));
    card.append(createNode('strong', tone ? toneClass(tone) : '', value));
    if (sub) card.append(createNode('small', '', sub));
    fragment.append(card);
  });
  container.replaceChildren(fragment);
}

function chart(canvasId, config) {
  const old = charts.get(canvasId);
  if (old) old.destroy();
  const instance = new Chart(byId(canvasId), config);
  charts.set(canvasId, instance);
}

function chartOptions(extra = {}) {
  return {
    responsive: true, maintainAspectRatio: false,
    interaction: { intersect: false, mode: 'index' },
    plugins: { legend: { display: false }, tooltip: { backgroundColor: '#07111f', titleColor: '#ecf4ff', bodyColor: '#cbd8eb', borderColor: 'rgba(158,185,220,.25)', borderWidth: 1, padding: 10 } },
    scales: { x: { grid: { display: false }, ticks: { color: '#657791', maxTicksLimit: 7, font: { size: 12 } } }, y: { grid: { color: 'rgba(158,185,220,.10)' }, ticks: { color: '#657791', font: { size: 12 } } } },
    ...extra
  };
}

function exposureTarget(statusId, marketRiskScore) {
  const score = Number(marketRiskScore?.score);
  const risk = Number.isFinite(score) ? Math.max(0, Math.min(100, score)) : 100;
  // リスクが10点上がるごとに、投資比率を10%下げる。
  // 市場判定はIBDの市場エクスポージャーの考え方に合わせて上限として使う。
  const riskBasedExposure = Math.max(0, 100 - Math.ceil(risk / 10) * 10);
  const statusCap = statusId === 'Correction' ? 20 : statusId === 'Pressure' ? 60 : 100;
  return Math.min(riskBasedExposure, statusCap);
}

function renderOverview(data) {
  const status = statusMeta[data.combinedStatus] || statusMeta.Correction;
  const exposure = exposureTarget(data.combinedStatus, data.marketRiskScore);
  const exposureTone = exposure <= 20 ? 'risk' : exposure <= 60 ? 'warn' : 'good';
  byId('market-status').textContent = status.en;
  byId('market-status').className = `state-value ${toneClass(status.tone)}`;
  byId('market-status-jp').textContent = status.jp;
  byId('exposure-value').textContent = `${exposure}%`;
  byId('exposure-value').className = toneClass(exposureTone);
  byId('market-driven-by').textContent = `市場判定：${data.combinedDrivenBy || '—'}。投資比率は市場判定とリスクスコアを組み合わせ、10%刻みで示します。`;
}

function renderRiskScore(score) {
  if (!score || score.score == null) { byId('risk-label').textContent = '算出不可'; return; }
  const value = Math.max(0, Math.min(100, Number(score.score)));
  const tone = value <= 20 ? 'good' : value <= 40 ? 'info' : value <= 60 ? 'warn' : 'risk';
  const color = tone === 'good' ? '#62d78b' : tone === 'info' ? '#4ba3ff' : tone === 'warn' ? '#ffc95c' : '#ff717d';
  byId('risk-score').firstChild.nodeValue = value.toFixed(1);
  byId('risk-label').textContent = score.label || '—';
  byId('risk-label').className = `headline ${toneClass(tone)}`;
  byId('risk-coverage').textContent = `採点カバレッジ ${number(score.dataCoveragePct, 0)}% · ${number(score.rawScore, 1)} / ${number(score.availableMaxPoints, 1)} 点を100点換算`;
  byId('risk-ring').style.setProperty('--score', value);
  byId('risk-ring').style.setProperty('--score-color', color);
  setPill('risk-raw', `Risk ${value.toFixed(1)} / 100`, tone);

  const table = byId('risk-metrics');
  const fragment = document.createDocumentFragment();
  (score.metrics || []).forEach(metric => {
    const ratio = Number(metric.maxPoints) ? Number(metric.score) / Number(metric.maxPoints) : 0;
    const metricTone = ratio >= .6 ? 'risk' : ratio > 0 ? 'warn' : 'good';
    const row = document.createElement('tr');
    row.append(termNode('td', '', metric.group));
    row.append(termNode('td', '', metric.name));
    row.append(createNode('td', '', metric.detail));
    const points = createNode('td', `points ${toneClass(metricTone)}`, `${number(metric.score, 1)} / ${number(metric.maxPoints, 1)}`);
    const bar = createNode('div', 'risk-bar'); const fill = document.createElement('i');
    fill.style.width = `${Math.min(100, ratio * 100)}%`; fill.style.backgroundColor = metricTone === 'risk' ? '#ff717d' : metricTone === 'warn' ? '#ffc95c' : '#62d78b';
    bar.append(fill); points.append(bar); row.append(points); fragment.append(row);
  });
  table.replaceChildren(fragment);
}

function renderRiskChange(change) {
  const summary = byId('risk-change-summary');
  const factors = byId('risk-change-factors');
  byId('risk-change-note').textContent = change?.note || '前回分の採点内訳がそろうと、変動理由を表示します。';
  if (!change || change.status !== 'ok' || change.previousScore == null || change.scoreChange == null) {
    setPill('risk-change-status', '蓄積中', 'muted');
    summary.replaceChildren(createNode('span', '', '前回との比較は、次回以降の更新から表示されます。'));
    factors.replaceChildren();
    return;
  }

  const prior = Number(change.previousScore);
  const delta = Number(change.scoreChange);
  const current = prior + delta;
  const scoreText = createNode('strong', delta > 0 ? 'tone-risk' : delta < 0 ? 'tone-good' : 'tone-muted', `${delta > 0 ? '+' : ''}${delta.toFixed(1)}点`);
  summary.replaceChildren(
    createNode('span', '', `${change.previousDate} の ${prior.toFixed(1)}点 → 今日の ${current.toFixed(1)}点`),
    scoreText,
    createNode('span', '', delta > 0 ? '市場リスクが上昇' : delta < 0 ? '市場リスクが低下' : '前回と同水準')
  );
  setPill('risk-change-status', delta > 0 ? 'リスク上昇' : delta < 0 ? 'リスク低下' : '変化なし', delta > 0 ? 'risk' : delta < 0 ? 'good' : 'muted');

  const fragment = document.createDocumentFragment();
  const items = Array.isArray(change.factors) ? change.factors : [];
  if (!items.length) {
    fragment.append(createNode('div', 'empty', '共通する採点項目に大きな変化はありません。'));
  } else {
    items.forEach(factor => {
      const deltaPoints = Number(factor.changeInRiskPoints || 0);
      const item = createNode('div', 'change-item');
      const label = document.createElement('strong');
      label.append(termNode('span', '', factor.group), document.createTextNode('｜'), termNode('span', '', factor.name));
      item.append(label);
      item.append(createNode('span', `change-points ${deltaPoints > 0 ? 'tone-risk' : 'tone-good'}`, `${deltaPoints > 0 ? '+' : ''}${deltaPoints.toFixed(1)}点`));
      item.append(createNode('small', '', `${factor.previousDetail || '前回値なし'} → ${factor.currentDetail || '今回値なし'}`));
      fragment.append(item);
    });
  }
  factors.replaceChildren(fragment);
}

function renderScoreValidation(validation) {
  const isPreliminary = validation?.status === 'preliminary';
  const isCollecting = validation?.status === 'collecting';
  setPill('score-validation-status', isPreliminary ? '暫定集計' : isCollecting ? '蓄積中' : '未計算', isPreliminary ? 'warn' : 'muted');
  replaceDataGrid('score-validation-summary', [
    { label: 'スコア記録', value: `${validation?.observationCount || 0}件`, sub: '同じ市場基準日は重複集計しません' },
    { label: '1か月後の確定実績', value: `${validation?.oneMonthMaturedCount || 0}件`, sub: `目安: ${validation?.recommendedMinSamples || 10}件以上` },
    { label: '3か月後の確定実績', value: `${validation?.threeMonthMaturedCount || 0}件`, sub: '63営業日経過後に確定' }
  ]);
  byId('score-validation-note').textContent = validation?.note || '検証データを蓄積中です。';

  const table = byId('score-validation-table');
  const fragment = document.createDocumentFragment();
  const bands = Array.isArray(validation?.bands) ? validation.bands : [];
  bands.forEach(band => {
    const row = document.createElement('tr');
    row.append(createNode('td', '', `${band.label}点`));
    const samples = createNode('td', '', `${band.oneMonthSampleSize || 0} / ${band.threeMonthSampleSize || 0}`);
    samples.append(createNode('span', 'subvalue', `記録 ${band.observationCount || 0}件`));
    row.append(samples, createValidationOutcomeCell(band.spyAverageReturn1m, band.qqqAverageReturn1m, band.spyWinRate1m, band.qqqWinRate1m), createValidationDrawdownCell(band.spyAverageMaxDrawdown1m, band.qqqAverageMaxDrawdown1m), createValidationOutcomeCell(band.spyAverageReturn3m, band.qqqAverageReturn3m, band.spyWinRate3m, band.qqqWinRate3m), createValidationDrawdownCell(band.spyAverageMaxDrawdown3m, band.qqqAverageMaxDrawdown3m));
    fragment.append(row);
  });
  if (!bands.length) {
    const row = document.createElement('tr'); const cell = createNode('td', 'empty', '検証データを準備中です。'); cell.colSpan = 6; row.append(cell); fragment.append(row);
  }
  table.replaceChildren(fragment);
}

function createValidationOutcomeCell(spyReturn, qqqReturn, spyWinRate, qqqWinRate) {
  const cell = createNode('td', 'outcome', `SPY ${signedPct(spyReturn)} / QQQ ${signedPct(qqqReturn)}`);
  cell.append(createNode('span', 'subvalue', `勝率 SPY ${pct(spyWinRate, 0)} / QQQ ${pct(qqqWinRate, 0)}`));
  return cell;
}

function createValidationDrawdownCell(spyDrawdown, qqqDrawdown) {
  const cell = createNode('td', '', `SPY ${pct(spyDrawdown)} / QQQ ${pct(qqqDrawdown)}`);
  cell.append(createNode('span', 'subvalue', '記録日から期間中の最大下落'));
  return cell;
}

function renderIndex(key, index) {
  const status = statusMeta[index.statusId] || statusMeta.Correction;
  byId(`index-${key}-name`).textContent = index.name;
  setPill(`index-${key}-status`, status.jp, status.tone);
  replaceMetrics(`index-${key}-metrics`, [
    ['データ基準日', index.dataAsOf || '—', 'muted'],
    ['調整後終値', number(index.latestAdjustedClose), index.isAboveSma50 ? 'good' : 'risk'],
    ['50日移動平均', number(index.sma50), index.isAboveSma50 ? 'good' : 'risk'],
    ['52週高値からの下落', pct(index.drawdownFromHighPct), Number(index.drawdownFromHighPct) <= -10 ? 'risk' : Number(index.drawdownFromHighPct) <= -3 ? 'warn' : 'good'],
    ['トレンド', trendLabel[index.trendState] || index.trendState || '—', index.trendState === 'ConfirmedUptrend' ? 'good' : 'warn'],
    ['有効 Distribution Day', `${index.distributionDaysActive ?? '--'} 日`, Number(index.distributionDaysActive) >= 6 ? 'risk' : Number(index.distributionDaysActive) >= 3 ? 'warn' : 'good'],
    ['最大の売り圧力', index.worstActiveDropPct != null ? `-${Math.abs(Number(index.worstActiveDropPct)).toFixed(2)}%` : 'なし', index.worstActiveDropPct != null ? 'warn' : 'good'],
    ['直近 FTD', index.lastFollowThroughDate || '未発生', index.lastFollowThroughDate ? 'good' : 'muted']
  ]);
  chart(`chart-${key}`, {
    type: 'line',
    data: { labels: index.chart.labels, datasets: [
      { data: index.chart.prices, borderColor: '#4ba3ff', backgroundColor: 'rgba(75,163,255,.10)', fill: true, tension: .18, borderWidth: 2, pointRadius: 0 },
      { data: index.chart.sma50, borderColor: '#ffc95c', borderDash: [5, 5], tension: .18, borderWidth: 1.5, pointRadius: 0 },
      { data: index.chart.distMarksActive, borderColor: '#ff717d', backgroundColor: '#ff717d', pointStyle: 'triangle', showLine: false, pointRadius: 5 },
      { data: index.chart.distMarksExpired, borderColor: '#657791', backgroundColor: '#657791', pointStyle: 'triangle', showLine: false, pointRadius: 3 }
    ]}, options: chartOptions()
  });
}

function renderBreadth(block) {
  if (!isAvailable(block)) { setPill('breadth-status', '取得不可', 'muted'); replaceDataGrid('breadth-data', [{ label: '市場ブレッドス', value: '取得不可', sub: block?.note || '—' }]); return; }
  const tone = block.status === 'partial' ? 'warn' : 'good';
  setPill('breadth-status', block.status === 'partial' ? '一部取得' : '稼働中', tone);
  replaceDataGrid('breadth-data', [
    { label: '50日線上', value: pct(block.aboveSma50Pct, 1), tone: Number(block.aboveSma50Pct) >= 60 ? 'good' : Number(block.aboveSma50Pct) <= 40 ? 'risk' : 'warn' },
    { label: '200日線上', value: pct(block.aboveSma200Pct, 1), tone: Number(block.aboveSma200Pct) >= 60 ? 'good' : Number(block.aboveSma200Pct) <= 40 ? 'risk' : 'warn' },
    { label: '52週 新高値 / 新安値', value: `${block.newHighs52Week} / ${block.newLows52Week}`, tone: Number(block.newHighs52Week) >= Number(block.newLows52Week) ? 'good' : 'risk' },
    { label: '20日 A/D変化', value: signed(block.adLineChange20d), sub: `${block.analyzedConstituents}/${block.expectedConstituents}銘柄・${number(block.coveragePct, 0)}%`, tone: Number(block.adLineChange20d) >= 0 ? 'good' : 'warn' }
  ]);
}

function renderVolatility(block) {
  const note = byId('volatility-note');
  if (!isAvailable(block)) {
    setPill('volatility-status', '取得不可', 'muted');
    replaceDataGrid('volatility-data', [{ label: 'VIX', value: '取得不可', sub: block?.note || '—' }]);
    note.textContent = block?.note || 'ボラティリティ指標を取得できませんでした。';
    return;
  }
  // ^VIX3Mは配信が止まることがあり、その場合はSPYの実現ボラティリティ比で期限構造を代替する。
  // 実現ボラの「短期>長期」は本物のVIX逆転より高頻度なので、同じ「逆転」表記・同じ警戒度にはしない。
  const usesRealized = block.termSource === 'RealizedVol';
  const slope = Number(block.termSlopePct);
  const stressed = usesRealized ? slope >= 20 : block.termStructure === 'Backwardation';
  const elevated = usesRealized && slope >= 5 && slope < 20;
  setPill('volatility-status', stressed ? '警戒' : usesRealized ? '代替指標で稼働' : '通常', stressed ? 'risk' : usesRealized ? 'warn' : 'good');
  replaceDataGrid('volatility-data', [
    { label: 'VIX', value: number(block.vix), sub: `20日平均 ${number(block.vixSma20)}`, tone: Number(block.vix) > Number(block.vixSma20) ? 'warn' : 'good' },
    usesRealized
      ? { label: 'SPY実現ボラ', value: `${number(block.realizedVolShortPct)} / ${number(block.realizedVolLongPct)}`, sub: '10日 / 63日（年率%）' }
      : { label: 'VIX3M', value: number(block.vix3m), sub: '3か月先の予想変動率' },
    usesRealized
      ? { label: '短期ボラの優勢度', value: stressed ? '強い短期優勢' : elevated ? 'やや短期優勢' : '長期優勢', sub: 'SPY実現ボラ比（代替）', tone: stressed ? 'risk' : elevated ? 'warn' : 'good' }
      : { label: '期限構造', value: stressed ? '逆転' : '順正常', sub: 'VIX対VIX3M', tone: stressed ? 'risk' : 'good' },
    { label: usesRealized ? '短期 対 長期' : 'VIX対VIX3M', value: signedPct(block.termSlopePct), tone: stressed ? 'risk' : elevated ? 'warn' : 'good' },
    // 分散リスクプレミアム：VIXの水準ではなく「現実の変動に対して割安か」を見る。
    // マイナス＝実現ボラがインプライドを上回る＝市場が現実の値動きに追いつけていない。
    { label: '分散リスクプレミアム', value: block.varianceRiskPremium == null ? '--' : `${Number(block.varianceRiskPremium) > 0 ? '+' : ''}${number(block.varianceRiskPremium)}`, sub: `VIX − 実現ボラ ${number(block.realizedVol21Pct)}（21日）`, tone: block.varianceRiskPremium == null ? 'muted' : Number(block.varianceRiskPremium) <= 0 ? 'risk' : Number(block.varianceRiskPremium) <= 1.5 ? 'warn' : 'good' }
  ]);
  note.textContent = block.note || '短期の警戒度が長期を上回る逆転は、市場ストレスの警戒信号です。';
}

function renderCredit(block) {
  if (!isAvailable(block)) { setPill('credit-status', '取得不可', 'muted'); replaceDataGrid('credit-data', [{ label: '信用リスク', value: '取得不可', sub: block?.note || '—' }]); return; }
  const spreadTone = Number(block.spread3m) < 0 ? 'risk' : 'good';
  setPill('credit-status', Number(block.spread3m) < 0 ? '慎重' : '安定', spreadTone);
  replaceDataGrid('credit-data', [
    { label: 'HYG 3か月', value: pct(block.hygReturn3m), sub: 'ハイイールド債' },
    { label: 'LQD 3か月', value: pct(block.lqdReturn3m), sub: '投資適格債' },
    { label: 'HY 対 IG', value: signedPct(block.spread3m), sub: '3か月の相対リターン', tone: spreadTone },
    { label: 'HY OAS', value: block.hyOasPct == null ? '未取得' : pct(block.hyOasPct), sub: block.hyOasChange1mBps == null ? '公表遅延または取得不可' : `1か月 ${Number(block.hyOasChange1mBps) > 0 ? '+' : ''}${number(block.hyOasChange1mBps, 0)}bp`, tone: block.hyOasChange1mBps == null ? 'muted' : Number(block.hyOasChange1mBps) > 0 ? 'risk' : 'good' }
  ]);
}

function renderAccumulation(data) {
  const block = data.marketBreadth;
  if (!isAvailable(block) || block.accumulationPct == null) {
    setPill('accumulation-status', '取得不可', 'muted');
    replaceDataGrid('accumulation-data', [{ label: '機関需給', value: '取得不可', sub: block?.note || '—' }]);
    return;
  }
  const accumulation = Number(block.accumulationPct);
  const stealth = block.stealthDistributionPct == null ? null : Number(block.stealthDistributionPct);
  const accumulationTone = accumulation >= 50 ? 'good' : accumulation >= 40 ? 'warn' : 'risk';
  const intensityTone = value => Number(value) >= 5 ? 'risk' : Number(value) >= 2.5 ? 'warn' : 'good';
  setPill('accumulation-status', accumulation >= 50 ? '買い優勢' : accumulation >= 40 ? '拮抗' : '売り優勢', accumulationTone);
  replaceDataGrid('accumulation-data', [
    { label: 'アキュムレーション比率', value: pct(accumulation, 1), sub: '50日の出来高が上昇日に偏る銘柄', tone: accumulationTone },
    { label: 'ステルス配分', value: stealth == null ? '--' : pct(stealth, 1), sub: '50日線上だが売り優勢', tone: stealth == null ? 'muted' : stealth >= 40 ? 'risk' : stealth >= 25 ? 'warn' : 'good' },
    { label: 'SPY 売り抜け強度', value: number(data.sp500?.distributionIntensity, 1), sub: '下落率×出来高比の合計', tone: intensityTone(data.sp500?.distributionIntensity) },
    { label: 'QQQ 売り抜け強度', value: number(data.nasdaq?.distributionIntensity, 1), sub: '下落率×出来高比の合計', tone: intensityTone(data.nasdaq?.distributionIntensity) }
  ]);
}

function renderStructure(data) {
  const block = data.marketBreadth;
  const rotation = data.sectorRotation?.rotationSpread1m;
  if (!isAvailable(block) || block.avgPairwiseCorrelation == null) {
    setPill('structure-status', '取得不可', 'muted');
    replaceDataGrid('structure-data', [{ label: '市場構造', value: '取得不可', sub: block?.note || '—' }]);
    return;
  }
  const correlation = Number(block.avgPairwiseCorrelation);
  const ratio = block.advanceRatioSma10 == null ? null : Number(block.advanceRatioSma10);
  const thrust = block.breadthThrustDetected === true;
  const correlationTone = correlation >= 0.70 ? 'risk' : correlation >= 0.55 ? 'warn' : 'good';
  setPill('structure-status', thrust ? 'Thrust 点灯' : correlation >= 0.70 ? '一括相場' : correlation >= 0.55 ? 'やや一括' : '個別物色', thrust ? 'good' : correlationTone);
  replaceDataGrid('structure-data', [
    { label: '銘柄間 平均ペア相関', value: number(correlation, 3), sub: '21日・高いほど分散が効かない', tone: correlationTone },
    { label: '10日騰落レシオ', value: ratio == null ? '--' : number(ratio, 3), sub: '上昇 / (上昇+下落) の10日平均', tone: ratio == null ? 'muted' : ratio <= 0.42 ? 'risk' : ratio <= 0.48 ? 'warn' : 'good' },
    { label: 'Breadth Thrust', value: thrust ? '点灯' : '未点灯', sub: '0.40以下→0.615以上を10日以内', tone: thrust ? 'good' : 'muted' },
    { label: 'ディフェンシブ優位度', value: rotation == null ? '--' : signedPct(rotation), sub: '守り − 攻め（1か月）', tone: rotation == null ? 'muted' : Number(rotation) >= 4 ? 'risk' : Number(rotation) >= 1.5 ? 'warn' : 'good' }
  ]);
}

function renderSector(block) {
  const table = byId('sector-table');
  const proxy = byId('proxy-metrics');
  if (!isAvailable(block) || !Array.isArray(block.sectors)) { setPill('sector-status', '取得不可', 'muted'); table.replaceChildren(); proxy.replaceChildren(createNode('div', 'empty', block?.note || 'セクターデータを取得できませんでした。')); return; }
  setPill('sector-status', '稼働中', 'good');
  const fragment = document.createDocumentFragment();
  block.sectors.forEach(item => {
    const row = document.createElement('tr');
    const name = document.createElement('td'); name.textContent = item.name; name.append(createNode('span', 'symbol', item.symbol));
    row.append(name, createNode('td', '', pct(item.return1m)), createNode('td', '', pct(item.return3m)));
    row.append(createNode('td', Number(item.relStrength3m) >= 0 ? 'tone-good' : 'tone-risk', signedPct(item.relStrength3m)));
    fragment.append(row);
  });
  table.replaceChildren(fragment);
  const proxies = block.breadthProxies || [];
  replaceMetrics('proxy-metrics', proxies.map(item => [`${item.name} (${item.symbol})`, `${signedPct(item.relStrength3m)} 対SPY`, Number(item.relStrength3m) >= 0 ? 'good' : 'warn']));
  byId('sector-note').textContent = `SPY: 1か月 ${pct(block.spyReturn1m)} / 3か月 ${pct(block.spyReturn3m)}。上位セクターは候補探しの出発点で、買い推奨ではありません。`;
}

function renderPutCall(block) {
  if (!isAvailable(block)) { setPill('putcall-status', '取得不可', 'muted'); replaceDataGrid('putcall-data', [{ label: 'Put / Call', value: '取得不可', sub: block?.note || '—' }]); byId('putcall-note').textContent = 'この日はオプションデータを取得できませんでした。'; return; }
  const percentile = block.percentileRank == null ? null : Number(block.percentileRank);
  const extreme = percentile != null && (percentile <= 20 || percentile >= 80);
  setPill('putcall-status', percentile == null ? '履歴蓄積中' : extreme ? '極端値を確認' : '中立', percentile == null ? 'muted' : extreme ? 'warn' : 'good');
  replaceDataGrid('putcall-data', [
    { label: 'Put / Call', value: number(block.ratio, 3), sub: '直近限月・出来高ベース' },
    { label: '10日平均', value: number(block.sma10, 3), sub: block.sma10 == null ? '10日分の履歴がそろうと表示' : '短期の基準線' },
    { label: '履歴順位', value: percentile == null ? '蓄積中' : `${number(percentile, 1)}%`, sub: `${block.historyDays || 0}日分の自己履歴`, tone: extreme ? 'warn' : 'good' },
    { label: '出来高', value: `${Number(block.putVolume || 0).toLocaleString()} / ${Number(block.callVolume || 0).toLocaleString()}`, sub: 'Put / Call' }
  ]);
  byId('putcall-note').textContent = block.note || '直近限月のみの参考指標です。';
}

function renderHealth(data) {
  const sources = [
    ['Put / Call', data.putCallRatio], ['セクター', data.sectorRotation], ['信用', data.creditRiskAppetite], ['VIX', data.volatilityRegime], ['ブレッドス', data.marketBreadth]
  ];
  const fragment = document.createDocumentFragment();
  sources.forEach(([name, block]) => {
    const row = createNode('div', 'health-item');
    row.append(createNode('span', '', name));
    const status = block?.status === 'ok' ? '正常' : block?.status === 'partial' ? '一部取得' : '未取得';
    row.append(createNode('strong', toneClass(block?.status === 'ok' ? 'good' : block?.status === 'partial' ? 'warn' : 'muted'), status));
    fragment.append(row);
  });
  byId('data-health').replaceChildren(fragment);
}

function renderHistory(history) {
  if (!Array.isArray(history) || history.length === 0) return;
  const scores = history.filter(item => Number.isFinite(Number(item.marketRiskScore)) && item.marketRiskScore != null);
  if (scores.length) chart('chart-history', { type: 'line', data: { labels: scores.map(item => item.date), datasets: [{ label: '市場リスクスコア', data: scores.map(item => item.marketRiskScore), borderColor: '#a990ff', backgroundColor: 'rgba(169,144,255,.12)', tension: .18, fill: true, borderWidth: 2, pointRadius: scores.length === 1 ? 4 : 2, pointHoverRadius: 5 }] }, options: chartOptions({ scales: { x: { grid: { display: false }, ticks: { color: '#657791', maxTicksLimit: 7, font: { size: 12 } } }, y: { min: 0, max: 100, grid: { color: 'rgba(158,185,220,.10)' }, ticks: { color: '#657791', font: { size: 12 }, callback: value => `${value}点` } } } }) });
  const pc = history.filter(item => item.putCallRatio != null);
  if (pc.length) chart('chart-putcall', { type: 'line', data: { labels: pc.map(item => item.date), datasets: [{ data: pc.map(item => item.putCallRatio), borderColor: '#a990ff', backgroundColor: 'rgba(169,144,255,.10)', fill: true, tension: .18, borderWidth: 2, pointRadius: 0 }] }, options: chartOptions() });
}

function jstCalendarDate(value) {
  const match = String(value || '').match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (!match) return null;
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
  return Number.isNaN(date.getTime()) ? null : date;
}

function jstToday() {
  const parts = new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Tokyo', year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(new Date());
  const get = type => Number(parts.find(part => part.type === type)?.value);
  return new Date(Date.UTC(get('year'), get('month') - 1, get('day')));
}

function completedWeekdaysSince(value) {
  const start = jstCalendarDate(value);
  const today = jstToday();
  if (!start || start > today) return null;
  let count = 0;
  const cursor = new Date(start);
  cursor.setUTCDate(cursor.getUTCDate() + 1);
  // 当日はまだ米国市場の終値が確定していない可能性があるため数えない。
  while (cursor < today) {
    const day = cursor.getUTCDay();
    if (day !== 0 && day !== 6) count += 1;
    cursor.setUTCDate(cursor.getUTCDate() + 1);
  }
  return count;
}

function showStaleWarning(lastUpdated) {
  // 市場基準日ではなく、生成時刻で更新停止を判定する。
  // 金曜終値を月曜の日本時間に表示するような週末またぎを、古いデータとして誤判定しないため。
  const elapsedBusinessDays = completedWeekdaysSince(lastUpdated);
  const stale = elapsedBusinessDays == null || elapsedBusinessDays >= 3;
  const banner = byId('stale-banner');
  banner.style.display = stale ? 'block' : 'none';
  if (stale) banner.textContent = elapsedBusinessDays == null
    ? '最終更新日時を確認できません。数値の鮮度を確認してください。'
    : `最終更新から米国市場の営業日が${elapsedBusinessDays}日経過しています。数値の鮮度を確認してください。`;
}

// 用語ツールチップ：ホバー・キーボードフォーカスに加え、タッチ端末はタップで開閉できるようにする。
// data-tipを持つ要素は5分ごとの再描画で作り直されるため、個別バインドせずdocument委譲で処理する。
function initTooltips() {
  const tip = createNode('div', 'md-tooltip', null);
  tip.id = 'md-tooltip';
  tip.setAttribute('role', 'tooltip');
  document.body.append(tip);
  let pinned = false;

  function position(target) {
    const rect = target.getBoundingClientRect();
    const tipRect = tip.getBoundingClientRect();
    let top = rect.top - tipRect.height - 10;
    if (top < 8) top = rect.bottom + 10;
    let left = rect.left + rect.width / 2 - tipRect.width / 2;
    left = Math.max(8, Math.min(left, window.innerWidth - tipRect.width - 8));
    tip.style.top = `${top}px`;
    tip.style.left = `${left}px`;
  }

  function show(target) {
    const text = target.getAttribute('data-tip');
    if (!text) return;
    tip.textContent = text;
    tip.classList.add('visible');
    position(target);
  }

  function hide() {
    tip.classList.remove('visible');
    pinned = false;
  }

  // mouseenter/leaveはバブリングしないため、documentへのキャプチャ登録で委譲する。
  document.addEventListener('mouseenter', event => {
    const target = event.target.closest?.('[data-tip]');
    if (target) show(target);
  }, true);
  document.addEventListener('mouseleave', event => {
    const target = event.target.closest?.('[data-tip]');
    if (target && !pinned) hide();
  }, true);
  document.addEventListener('focusin', event => {
    const target = event.target.closest?.('[data-tip]');
    if (target) show(target);
  });
  document.addEventListener('focusout', event => {
    const target = event.target.closest?.('[data-tip]');
    if (target && !pinned) hide();
  });
  document.addEventListener('click', event => {
    const target = event.target.closest?.('[data-tip]');
    if (!target) { hide(); return; }
    if (pinned) { hide(); } else { pinned = true; show(target); }
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') hide(); });
  window.addEventListener('scroll', hide, true);
  window.addEventListener('resize', hide);
}

async function fetchJson(path, fallback) {
  const response = await fetch(`${path}?t=${Date.now()}`, { cache: 'no-store' });
  if (!response.ok) return fallback;
  return response.json();
}

async function loadDashboard() {
  try {
    const [data, history] = await Promise.all([fetchJson('data.json'), fetchJson('history.json', [])]);
    if (!data) throw new Error('data.json is unavailable');
    lastLoadedAt = Date.now();
    const sourceAsOf = data.marketDataAsOf || data.sp500?.dataAsOf;
    const generatedAt = data.lastUpdated || '—';
    byId('update-time').textContent = sourceAsOf ? `${sourceAsOf} 時点 · 更新 ${generatedAt} JST` : `更新 ${generatedAt} JST`;
    showStaleWarning(data.lastUpdated);
    renderOverview(data); renderRiskScore(data.marketRiskScore); renderRiskChange(data.marketRiskChange); renderHealth(data);
    renderIndex('spy', data.sp500); renderIndex('qqq', data.nasdaq);
    renderBreadth(data.marketBreadth); renderVolatility(data.volatilityRegime); renderCredit(data.creditRiskAppetite);
    renderAccumulation(data); renderStructure(data);
    renderSector(data.sectorRotation); renderPutCall(data.putCallRatio); renderHistory(history); renderScoreValidation(data.scoreValidation);
  } catch (error) {
    console.error(error); byId('update-time').textContent = 'データ読込エラー'; byId('stale-banner').style.display = 'block'; byId('stale-banner').textContent = 'データを読み込めませんでした。data.json と更新ワークフローを確認してください。';
  }
}

initTooltips();
loadDashboard();
setInterval(loadDashboard, 5 * 60 * 1000);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible' && Date.now() - lastLoadedAt > 60000) loadDashboard(); });
  
