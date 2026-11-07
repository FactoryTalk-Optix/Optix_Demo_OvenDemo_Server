(function () {
  // Palettes mirror the Light/Dark StyleSheet tokens of the project.
  var PALETTES = {
    light: { ink: '#16304d', muted: '#5f7690', line: '#c7d0d5', grid: '#dfe7ee', a: '#a9caec', p: '#4a8ed2', q: '#235d9f', oee: '#16395f', energy: '#2c72c0' },
    dark: { ink: '#f1f5f9', muted: '#c0cddc', line: '#4b5d73', grid: '#4b5d73', a: '#3f86d6', p: '#6aa5e6', q: '#9cc4f0', oee: '#d4e4f6', energy: '#6aa5e6' }
  };
  var P = PALETTES[document.body.getAttribute('data-theme')] || PALETTES.light;
  var oeeChart = echarts.init(document.getElementById('oee'));
  var energyChart = echarts.init(document.getElementById('energy'));
  var lastText = null;

  function pct(v) { return v == null ? '--' : v.toFixed(1) + '%'; }

  function base(title, bottom) {
    return {
      animationDuration: 300,
      textStyle: { fontFamily: "Bahnschrift, Lato, 'Segoe UI', sans-serif" },
      title: { text: title, left: 16, top: 12, textStyle: { fontSize: 14, fontWeight: 600, color: P.ink } },
      grid: { left: 16, right: 48, top: 48, bottom: bottom, containLabel: true },
      yAxis: { type: 'category', inverse: true, axisTick: { show: false }, axisLine: { lineStyle: { color: P.line } }, axisLabel: { color: P.ink } }
    };
  }

  function render(data) {
    var ovens = data.ovens || [];
    document.getElementById('empty').style.display = ovens.length ? 'none' : 'flex';
    var ids = ovens.map(function (o) { return o.id; });
    var online = {};
    ovens.forEach(function (o) { online[o.id] = o.state === 'Online'; });
    var labelFmt = function (id) { return online[id] ? id : id + ' (offline)'; };

    var oee = base('OEE by oven', 40);
    oee.legend = { bottom: 8, itemWidth: 10, itemHeight: 10, textStyle: { color: P.muted } };
    oee.tooltip = { trigger: 'axis', axisPointer: { type: 'shadow' }, valueFormatter: pct };
    oee.xAxis = { type: 'value', min: 0, max: 100, axisLabel: { color: P.muted, formatter: '{value}%' }, splitLine: { lineStyle: { color: P.grid } } };
    oee.yAxis.data = ids;
    oee.yAxis.axisLabel.formatter = labelFmt;
    oee.series = [
      ['Availability', 'availability', P.a],
      ['Performance', 'performance', P.p],
      ['Quality', 'quality', P.q],
      ['OEE', 'oee', P.oee]
    ].map(function (s) {
      return {
        name: s[0], type: 'bar', barMaxWidth: 14, itemStyle: { color: s[2] },
        label: { show: s[0] === 'OEE', position: 'right', color: P.ink, formatter: function (p) { return p.value == null ? '' : pct(p.value); } },
        data: ovens.map(function (o) { return o[s[1]]; })
      };
    });
    oeeChart.setOption(oee, true);

    var energy = base('Energy today by oven (kWh)', 16);
    energy.tooltip = {
      trigger: 'axis', axisPointer: { type: 'shadow' },
      formatter: function (items) {
        var o = ovens[items[0].dataIndex];
        return o.id + '<br/>Energy: ' + o.energyKwh.toFixed(3) + ' kWh<br/>Cycles: ' + o.cycles;
      }
    };
    energy.xAxis = { type: 'value', min: 0, axisLabel: { color: P.muted }, splitLine: { lineStyle: { color: P.grid } } };
    energy.yAxis.data = ids;
    energy.yAxis.axisLabel.formatter = labelFmt;
    energy.series = [{
      name: 'Energy', type: 'bar', barMaxWidth: 18, itemStyle: { color: P.energy },
      label: { show: true, position: 'right', color: P.ink, formatter: function (p) { return p.value.toFixed(3); } },
      data: ovens.map(function (o) { return o.energyKwh; })
    }];
    energyChart.setOption(energy, true);
  }

  // Data arrives as a script tag: fetch() is blocked on file:// pages (native presentation engine).
  function poll() {
    var tag = document.createElement('script');
    tag.onload = function () {
      var data = window.OVEN_DATA;
      tag.remove();
      var text = JSON.stringify(data);
      if (data && text !== lastText) { lastText = text; render(data); }
    };
    tag.onerror = function () { tag.remove(); };
    tag.src = 'oven-analytics-data.js?ts=' + Date.now();
    document.head.appendChild(tag);
  }

  new ResizeObserver(function () { oeeChart.resize(); energyChart.resize(); }).observe(document.getElementById('root'));
  poll();
  setInterval(poll, 1000);
})();
