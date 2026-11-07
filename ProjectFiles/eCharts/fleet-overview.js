(function () {
  var TEXTS = {
    'en-US': {
      statusTitle: 'Fleet status', running: 'Running', idle: 'Idle', offline: 'Offline', fault: 'Fault', ovens: 'ovens', online: '{n} of {m} online',
      activityTitle: 'Recent activity', empty: 'No activity yet', noOvens: 'No ovens registered', now: 'now', min: '{n} min ago', hour: '{n} h ago',
      connected: '{id} connected', disconnected: '{id} disconnected', fault_: '{id} process fault', faultCleared: '{id} fault cleared',
      cycleStarted: '{id} cycle started', cycleCompleted: '{id} cycle completed', cycleRejected: '{id} cycle rejected', cycleStopped: '{id} cycle stopped',
      recipeApplied: '{id} recipe {a} applied', onboardRequested: '{id} requested onboarding', onboarded: '{id} onboarded', declined: '{id} onboarding declined', offboarded: '{id} offboarded',
      recipeDraft: 'Recipe {id} moved back to draft', recipeInReview: 'Recipe {id} sent for review', recipeApproved: 'Recipe {id} approved', recipeDeprecated: 'Recipe {id} deprecated', recipeSent: 'Recipe {id} sent to the fleet'
    },
    'it-IT': {
      statusTitle: 'Stato della flotta', running: 'In produzione', idle: 'Inattivo', offline: 'Offline', fault: 'Guasto', ovens: 'forni', online: '{n} di {m} online',
      activityTitle: 'Attività recenti', empty: 'Nessuna attività', noOvens: 'Nessun forno registrato', now: 'ora', min: '{n} min fa', hour: '{n} h fa',
      connected: '{id} connesso', disconnected: '{id} disconnesso', fault_: '{id} guasto di processo', faultCleared: '{id} guasto risolto',
      cycleStarted: '{id} ciclo avviato', cycleCompleted: '{id} ciclo completato', cycleRejected: '{id} ciclo scartato', cycleStopped: '{id} ciclo interrotto',
      recipeApplied: '{id} ricetta {a} applicata', onboardRequested: '{id} ha richiesto l\'onboarding', onboarded: '{id} onboarding completato', declined: '{id} onboarding rifiutato', offboarded: '{id} offboarding eseguito',
      recipeDraft: 'Ricetta {id} riportata in bozza', recipeInReview: 'Ricetta {id} inviata in revisione', recipeApproved: 'Ricetta {id} approvata', recipeDeprecated: 'Ricetta {id} deprecata', recipeSent: 'Ricetta {id} inviata alla flotta'
    },
    'es-ES': {
      statusTitle: 'Estado de la flota', running: 'En producción', idle: 'Inactivo', offline: 'Sin conexión', fault: 'Fallo', ovens: 'hornos', online: '{n} de {m} en línea',
      activityTitle: 'Actividad reciente', empty: 'Sin actividad', noOvens: 'Ningún horno registrado', now: 'ahora', min: 'hace {n} min', hour: 'hace {n} h',
      connected: '{id} conectado', disconnected: '{id} desconectado', fault_: '{id} fallo de proceso', faultCleared: '{id} fallo resuelto',
      cycleStarted: '{id} ciclo iniciado', cycleCompleted: '{id} ciclo completado', cycleRejected: '{id} ciclo rechazado', cycleStopped: '{id} ciclo detenido',
      recipeApplied: '{id} receta {a} aplicada', onboardRequested: '{id} solicitó la incorporación', onboarded: '{id} incorporado', declined: '{id} incorporación rechazada', offboarded: '{id} dado de baja',
      recipeDraft: 'Receta {id} devuelta a borrador', recipeInReview: 'Receta {id} enviada a revisión', recipeApproved: 'Receta {id} aprobada', recipeDeprecated: 'Receta {id} obsoleta', recipeSent: 'Receta {id} enviada a la flota'
    },
    'fr-FR': {
      statusTitle: 'État de la flotte', running: 'En production', idle: 'Inactif', offline: 'Hors ligne', fault: 'Défaut', ovens: 'fours', online: '{n} sur {m} en ligne',
      activityTitle: 'Activité récente', empty: 'Aucune activité', noOvens: 'Aucun four enregistré', now: 'maintenant', min: 'il y a {n} min', hour: 'il y a {n} h',
      connected: '{id} connecté', disconnected: '{id} déconnecté', fault_: '{id} défaut de process', faultCleared: '{id} défaut résolu',
      cycleStarted: '{id} cycle démarré', cycleCompleted: '{id} cycle terminé', cycleRejected: '{id} cycle rejeté', cycleStopped: '{id} cycle arrêté',
      recipeApplied: '{id} recette {a} appliquée', onboardRequested: '{id} a demandé l\'intégration', onboarded: '{id} intégré', declined: '{id} intégration refusée', offboarded: '{id} retiré',
      recipeDraft: 'Recette {id} repassée en brouillon', recipeInReview: 'Recette {id} envoyée en revue', recipeApproved: 'Recette {id} approuvée', recipeDeprecated: 'Recette {id} dépréciée', recipeSent: 'Recette {id} envoyée à la flotte'
    },
    'de-DE': {
      statusTitle: 'Flottenstatus', running: 'In Produktion', idle: 'Leerlauf', offline: 'Offline', fault: 'Störung', ovens: 'Öfen', online: '{n} von {m} online',
      activityTitle: 'Letzte Aktivitäten', empty: 'Keine Aktivität', noOvens: 'Keine Öfen registriert', now: 'jetzt', min: 'vor {n} Min.', hour: 'vor {n} Std.',
      connected: '{id} verbunden', disconnected: '{id} getrennt', fault_: '{id} Prozessstörung', faultCleared: '{id} Störung behoben',
      cycleStarted: '{id} Zyklus gestartet', cycleCompleted: '{id} Zyklus abgeschlossen', cycleRejected: '{id} Zyklus verworfen', cycleStopped: '{id} Zyklus gestoppt',
      recipeApplied: '{id} Rezept {a} angewendet', onboardRequested: '{id} hat Onboarding angefragt', onboarded: '{id} aufgenommen', declined: '{id} Onboarding abgelehnt', offboarded: '{id} entfernt (Offboarding)',
      recipeDraft: 'Rezept {id} zurück auf Entwurf', recipeInReview: 'Rezept {id} zur Prüfung gesendet', recipeApproved: 'Rezept {id} freigegeben', recipeDeprecated: 'Rezept {id} veraltet', recipeSent: 'Rezept {id} an die Flotte gesendet'
    },
    'zh-CN': {
      statusTitle: '设备群状态', running: '生产中', idle: '空闲', offline: '离线', fault: '故障', ovens: '烤箱', online: '{n}/{m} 在线',
      activityTitle: '最近活动', empty: '暂无活动', noOvens: '未注册烤箱', now: '刚刚', min: '{n} 分钟前', hour: '{n} 小时前',
      connected: '{id} 已连接', disconnected: '{id} 已断开', fault_: '{id} 工艺故障', faultCleared: '{id} 故障已清除',
      cycleStarted: '{id} 周期已开始', cycleCompleted: '{id} 周期已完成', cycleRejected: '{id} 周期被拒', cycleStopped: '{id} 周期已停止',
      recipeApplied: '{id} 已应用配方 {a}', onboardRequested: '{id} 请求接入', onboarded: '{id} 已接入', declined: '{id} 接入被拒绝', offboarded: '{id} 已移出',
      recipeDraft: '配方 {id} 已退回草稿', recipeInReview: '配方 {id} 已提交审核', recipeApproved: '配方 {id} 已批准', recipeDeprecated: '配方 {id} 已弃用', recipeSent: '配方 {id} 已发送到设备群'
    }
  };
  var STATES = ['running', 'idle', 'offline', 'fault'];
  // Event kind -> dot color token.
  var DOTS = {
    connected: 'running', faultCleared: 'running', cycleCompleted: 'running', onboarded: 'running', recipeApproved: 'running',
    disconnected: 'offline', fault: 'fault', cycleRejected: 'warn', declined: 'offline', offboarded: 'offline', onboardRequested: 'warn', recipeDeprecated: 'warn',
    cycleStarted: 'idle', cycleStopped: 'idle', recipeApplied: 'idle', recipeDraft: 'idle', recipeInReview: 'idle', recipeSent: 'idle'
  };
  var ROW = 38;

  var body = document.body;
  var lang = body.getAttribute('data-lang') || 'en-US';
  var L = TEXTS[lang] || TEXTS['en-US'];
  var chart = echarts.init(document.getElementById('chart'));
  var data = null;
  var lastText = null;
  var lastCounts = null;

  function $(id) { return document.getElementById(id); }
  function css(name) { return getComputedStyle(body).getPropertyValue(name).trim(); }
  function fill(template, values) { return template.replace(/\{(\w+)\}/g, function (m, key) { return values[key] == null ? '' : values[key]; }); }
  function esc(text) { return String(text).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  function when(ms) {
    var minutes = Math.floor((Date.now() - ms) / 60000);
    if (minutes < 1) return L.now;
    if (minutes < 60) return fill(L.min, { n: minutes });
    if (minutes < 1440) return fill(L.hour, { n: Math.floor(minutes / 60) });
    return new Date(ms).toLocaleString(lang, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
  }

  function renderStatus() {
    var counts = data.counts || {};
    var total = data.total || 0;
    var key = JSON.stringify([counts, total]);
    var online = total - (counts.offline || 0);
    $('statusTitle').textContent = L.statusTitle;
    $('onlineText').textContent = total ? fill(L.online, { n: online, m: total }) : L.noOvens;
    $('total').textContent = total;
    $('totalLabel').textContent = L.ovens;
    if (key === lastCounts) return;
    lastCounts = key;

    $('legend').innerHTML = STATES.map(function (s) {
      return '<li><span class="dot" style="background:' + css('--' + s) + '"></span><span class="name">' + esc(L[s]) + '</span><span class="count">' + (counts[s] || 0) + '</span></li>';
    }).join('');
    chart.setOption({
      animationDuration: 300,
      series: [{
        type: 'pie', radius: ['66%', '92%'], avoidLabelOverlap: false, silent: true, label: { show: false }, labelLine: { show: false },
        itemStyle: { borderColor: css('--card'), borderWidth: 3 },
        data: total
          ? STATES.map(function (s) { return { name: s, value: counts[s] || 0, itemStyle: { color: css('--' + s) } }; })
          : [{ value: 1, itemStyle: { color: css('--grid') } }]
      }]
    }, true);
  }

  function renderFeed() {
    var list = $('feed');
    $('activityTitle').textContent = L.activityTitle;
    var events = data.events || [];
    $('empty').textContent = L.empty;
    $('empty').style.display = events.length ? 'none' : 'flex';
    list.style.display = events.length ? 'block' : 'none';
    var fit = Math.max(1, Math.floor(list.clientHeight / ROW));
    list.innerHTML = events.slice(0, fit).map(function (e) {
      var template = L[e.k === 'fault' ? 'fault_' : e.k] || e.k;
      var text = esc(fill(template, { id: '\u0001', a: e.a })).replace('\u0001', '<b>' + esc(e.id) + '</b>');
      return '<li><span class="dot" style="background:' + css('--' + (DOTS[e.k] || 'idle')) + '"></span><span class="text">' + text + '</span><span class="when">' + esc(when(e.t)) + '</span></li>';
    }).join('');
  }

  function render() {
    if (!data) return;
    renderStatus();
    renderFeed();
  }

  // Data arrives as a script tag: fetch() is blocked on file:// pages (native presentation engine).
  function poll() {
    var tag = document.createElement('script');
    tag.onload = function () {
      var next = window.FLEET_OVERVIEW;
      tag.remove();
      if (next) { data = next; }
      render();
    };
    tag.onerror = function () { tag.remove(); };
    tag.src = 'fleet-overview-data.js?ts=' + Date.now();
    document.head.appendChild(tag);
  }

  new ResizeObserver(function () { chart.resize(); render(); }).observe(document.getElementById('root'));
  poll();
  setInterval(poll, 1000);
})();
