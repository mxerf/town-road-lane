// Miniature top-down previews of marking styles — shown in dropdown options
// and accordion row headers instead of (or next to) text-only labels, so a
// style reads at a glance without parsing words.
//
// cohtml SVG rules honoured here:
//   - explicit fill/stroke attribute values only (currentColor resolves to
//     black regardless of CSS color, var() does not resolve in presentation
//     attributes) — every colour below is a literal;
//   - no stroke-dasharray (unverified in this cohtml build) — dashes are
//     drawn as individual rects;
//   - content outside the viewBox is clipped by the svg viewport, which the
//     diagonal hatch lines rely on.

// Marking paint on dark panel surfaces — same near-white as the game's text.
const PAINT = "rgba(240, 251, 255, 0.92)";

// Area fill palette. Literals matching what the fills look like on the road
// (concrete slab, G87 yellow waffle, white/yellow hatching, bike/bus lanes).
const CONCRETE = "#97a0a8";
const YELLOW = "#f2c94c";
const BIKE_GREEN = "#4aa054";
const BUS_RED = "#b04a38";

// MarkingStyle enum on the C# side: 0 Solid, 1 Dashed, 2 G87 Solid,
// 3 G87 Dashed, 4 Double Solid. G87 variants share the vanilla geometry —
// callers that need to telegraph "G87" add a text mark next to the preview.
export const isG87LineStyle = (style: number): boolean => style === 2 || style === 3;

export const LineStylePreview = ({
  style,
  width = 36,
  height = 10,
}: {
  style: number;
  width?: number;
  height?: number;
}) => {
  const dashed = style === 1 || style === 3;
  const double = style === 4;
  return (
    <svg width={width} height={height} viewBox="0 0 36 10" fill="none">
      {double ? (
        <>
          <rect x="1" y="2.5" width="34" height="2" fill={PAINT} />
          <rect x="1" y="5.5" width="34" height="2" fill={PAINT} />
        </>
      ) : dashed ? (
        <>
          <rect x="1" y="4" width="6" height="2" fill={PAINT} />
          <rect x="10.5" y="4" width="6" height="2" fill={PAINT} />
          <rect x="20" y="4" width="6" height="2" fill={PAINT} />
          <rect x="29.5" y="4" width="5.5" height="2" fill={PAINT} />
        </>
      ) : (
        <rect x="1" y="4" width="34" height="2" fill={PAINT} />
      )}
    </svg>
  );
};

// Diagonal hatch: long ↗ lines marching across the square; the svg viewport
// clips the overhang. step controls density (sparse vs dense stripes).
const hatch = (color: string, step: number, strokeWidth = 1.6) => {
  const lines = [];
  for (let x = -14 + step; x < 28; x += step) {
    lines.push(
      <line key={x} x1={x} y1={14} x2={x + 14} y2={0} stroke={color} strokeWidth={strokeWidth} />,
    );
  }
  return lines;
};

// Index matches kStyleSurfaceNames / areaStyle.* i18n keys:
// 0 Concrete, 1 Junction box (G87), 2 White stripes, 3 White stripes sparse,
// 4 Yellow stripes, 5 Green bike lane, 6 Red bus lane.
export const AreaStylePreview = ({ styleId, size = 14 }: { styleId: number; size?: number }) => {
  const solidFill =
    styleId === 0 ? CONCRETE : styleId === 5 ? BIKE_GREEN : styleId === 6 ? BUS_RED : null;
  return (
    <svg width={size} height={size} viewBox="0 0 14 14" fill="none">
      {solidFill ? (
        <rect x="0.5" y="0.5" width="13" height="13" rx="2" fill={solidFill} />
      ) : (
        <>
          <rect x="0.5" y="0.5" width="13" height="13" rx="2" fill="rgba(255, 255, 255, 0.06)" />
          {styleId === 1 && (
            <>
              {hatch(YELLOW, 4.5, 1.3)}
              {/* waffle = hatching both ways */}
              {[-9, -4.5, 0, 4.5, 9].map((x) => (
                <line key={`b${x}`} x1={x} y1={0} x2={x + 14} y2={14} stroke={YELLOW} strokeWidth={1.3} />
              ))}
            </>
          )}
          {styleId === 2 && hatch(PAINT, 4)}
          {styleId === 3 && hatch(PAINT, 7)}
          {styleId === 4 && hatch(YELLOW, 4)}
        </>
      )}
      <rect
        x="0.5"
        y="0.5"
        width="13"
        height="13"
        rx="2"
        stroke="rgba(255, 255, 255, 0.28)"
        strokeWidth="1"
      />
    </svg>
  );
};
