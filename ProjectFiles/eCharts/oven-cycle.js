(function () {
  var body = document.body;
  var oven = body.getAttribute('data-oven');
  var chart = echarts.init(document.getElementById('chart'));
  var status = document.getElementById('status');
  var lastText = null;

  function css(name) { return getComputedStyle(body).getPropertyValue(name).trim(); }
  function fmt(s) { s = Math.max(0, Math.round(s)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }

  function render(d) {
    var ink = css('--ink'), muted = css('--muted'), grid = css('--grid');
    var bands = [css('--band-a'), css('--band-b')];
    var phases = d.phases || [];
    var points = [];
    phases.forEach(function (p) { points.push([p.start, p.setpoint], [p.end, p.setpoint]); });
    var peak = Math.max.apply(null, [50, d.temperature].concat(phases.map(function (p) { return p.setpoint; })));
    var yMax = Math.ceil(peak * 1.15 / 50) * 50;
    var running = d.state === 1;
    var total = Math.max(d.total || 0, d.cycleElapsed || 0, 1);

    chart.setOption({
      animation: false,
      textStyle: { fontFamily: "Bahnschrift, Lato, 'Segoe UI', sans-serif" },
      grid: { left: 52, right: 20, top: 40, bottom: 34 },
      tooltip: {
        trigger: 'axis', axisPointer: { type: 'line' },
        formatter: function (items) {
          return fmt(items[0].axisValue) + '<br>' + items.map(function (i) { return i.marker + i.seriesName + ': ' + i.value[1] + ' °C'; }).join('<br>');
        }
      },
      legend: { top: 6, left: 8, itemWidth: 18, itemHeight: 3, textStyle: { color: muted }, data: ['Recipe setpoint', 'Actual'] },
      xAxis: { type: 'value', min: 0, max: total, axisLabel: { formatter: fmt, color: muted }, splitLine: { lineStyle: { color: grid } } },
      yAxis: { type: 'value', min: 0, max: yMax, axisLabel: { formatter: '{value}°', color: muted }, splitLine: { lineStyle: { color: grid } } },
      series: [
        {
          name: 'Recipe setpoint', type: 'line', data: points, showSymbol: false,
          lineStyle: { width: 2, type: 'dashed', color: css('--setpoint') }, itemStyle: { color: css('--setpoint') },
          markArea: {
            silent: true, label: { color: muted, fontSize: 11, position: 'insideTop' },
            data: phases.map(function (p, i) { return [{ name: p.name, xAxis: p.start, itemStyle: { color: bands[i % 2] } }, { xAxis: p.end }]; })
          },
          markLine: running ? {
            silent: true, symbol: 'none', animation: false, lineStyle: { color: css('--now'), width: 2 },
            label: { formatter: 'now ' + fmt(d.cycleElapsed), color: css('--now') }, data: [{ xAxis: d.cycleElapsed }]
          } : { data: [] }
        },
        {
          name: 'Actual', type: 'line', data: d.actual || [], showSymbol: false,
          lineStyle: { width: 3, color: css('--actual') }, itemStyle: { color: css('--actual') },
          areaStyle: { color: css('--actual'), opacity: 0.12 }
        }
      ]
    }, { replaceMerge: ['series'] });

    var phase = phases[Math.max(0, d.phase - 1)];
    status.textContent = !d.online ? 'Offline'
      : running ? (phase ? phase.name + ' · ' : '') + d.temperature.toFixed(1) + ' °C → ' + d.setpoint + ' °C'
      : 'Idle · ' + d.temperature.toFixed(1) + ' °C';
    status.style.color = ink;
  }

  // Data arrives as a script tag: fetch() is blocked on file:// pages (native presentation engine).
  function poll() {
    var tag = document.createElement('script');
    tag.onload = function () {
      var data = window.OVEN_CYCLE;
      tag.remove();
      var text = JSON.stringify(data);
      if (data && text !== lastText) { lastText = text; render(data); }
    };
    tag.onerror = function () { tag.remove(); };
    tag.src = oven + '-data.js?ts=' + Date.now();
    document.head.appendChild(tag);
  }

  new ResizeObserver(function () { chart.resize(); }).observe(document.getElementById('chart'));
  poll();
  setInterval(poll, 1000);
})();
