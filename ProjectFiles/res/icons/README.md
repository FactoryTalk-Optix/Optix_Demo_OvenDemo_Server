# Icons · 16×16 SVG

Complete icon set for Server Supervisory PoC in **Apple-minimal style**, brand color `#235d9f`, with rounded strokes.

## Structure

```
icons/
├── gallery.html         ← preview all icons
├── fleet.svg           ← navigation: fleet overview
├── ovens.svg           ← navigation: ovens / drill-down
├── alarms.svg          ← navigation: alarms & events
├── recipes.svg         ← navigation: recipes manager
├── oee.svg             ← navigation: OEE analytics
├── loggers.svg         ← navigation: data loggers
├── connectivity.svg    ← navigation: MQTT / OPC-UA
├── opc-ua.svg          ← navigation: OPC-UA server
├── mqtt.svg            ← navigation: MQTT broker/client
├── users.svg           ← admin: users & roles
├── security.svg        ← admin: security, certificates
├── arrow-left.svg      ← direction: back / previous
├── arrow-right.svg     ← direction: forward / next
├── chevron-right.svg   ← action: expand, drill-down, open
├── check-circle.svg    ← action: acknowledge, confirm, apply
├── close.svg           ← action: dismiss, cancel, remove
├── filter.svg          ← action: filter list
├── sliders.svg         ← action: advanced settings / filters
├── settings.svg        ← action: configure, setup
├── clock.svg           ← time: schedule, duration, when
├── download.svg        ← action: export, save CSV
├── download-alt.svg    ← action: download (arrow down variant)
├── upload.svg          ← action: import, upload file
├── upload-alt.svg      ← action: upload (arrow up variant)
├── search.svg          ← action: search, find
├── archive.svg         ← action: archive, store
├── aperture.svg        ← action: visibility, focus
├── eye.svg             ← action: watch, observe, monitor
├── send.svg            ← action: send command, broadcast, transmit
├── plus-circle.svg     ← action: add, create new item
├── refresh.svg         ← action: refresh, reload, reload from source
└── swap.svg            ← action: swap, exchange, reverse direction
```

## Usage in HTML

### Inline Image

```html
<img src="icons/fleet.svg" width="16" height="16" alt="Fleet overview" />
```

### As CSS Background

```css
.nav-icon-fleet {
  background-image: url('icons/fleet.svg');
  background-size: 16px 16px;
  background-repeat: no-repeat;
  width: 16px;
  height: 16px;
}
```

### In Navigation (HTML)

```html
<a class="nav-item" href="server-fleet-overview.html">
  <span class="ic">
    <img src="icons/fleet.svg" width="16" />
  </span>
  Fleet Overview
</a>
```

### Button with Icon

```html
<button class="btn">
  <img src="icons/download.svg" width="14" />
  Export CSV
</button>
```

## Customization

All SVG files use:
- **viewBox**: `0 0 16 16`
- **stroke color**: `#235d9f` (brand color)
- **stroke-width**: `1.2px` (readable at 16px)
- **fill**: `none` (outline style, except for some details like dots or filled elements)
- **stroke-linecap**: `round`
- **stroke-linejoin**: `round`

To change color globally, replace `#235d9f` in all SVG files, or use CSS filter:

```css
img[src*=".svg"] {
  filter: brightness(0) saturate(100%) invert(22%) sepia(45%) saturate(1280%) hue-rotate(188deg);
}
```

## Preview

Open `gallery.html` in a browser to see all icons with their names and recommended usage.

## Notes

- **16×16 is the baseline**, but SVGs scale to any size without quality loss
- Use at **18–24px for navigation**, **14px for buttons**, **32px+ for hero/section icons**
- All icons are **monochrome outline style** — compatible with brand color and light/dark themes
- No external dependencies, pure SVG
