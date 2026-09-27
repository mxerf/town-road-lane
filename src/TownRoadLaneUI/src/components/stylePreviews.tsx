// Small top-down previews of marking styles for dropdown options and row headers.
//
// cohtml SVG constraints:
//   - Only literal fill/stroke values: currentColor resolves to black whatever the CSS
//     color, and var() does not resolve in presentation attributes.
//   - No stroke-dasharray (untested in cohtml); dashes are separate rects.
//   - The svg viewport clips content outside the viewBox, which the hatch lines rely on.

// Same near-white as the game's text.
const PAINT = "rgba(240, 251, 255, 0.92)";

// Area fill colours, close to how the fills look on the road.
const CONCRETE = "#97a0a8";
const YELLOW = "#f2c94c";
const BIKE_GREEN = "#4aa054";
const BUS_RED = "#b04a38";

// MarkingStyle values: 0 Solid, 1 Dashed, 2 G87 Solid, 3 G87 Dashed, 4 Double Solid,
// 5 Dashed short, 6 G87 Yellow, 7 G87 Yellow Dashed, 8 Dashed long, 9 Curb, 10-13 vanilla
// yellow (solid, dashed, double, solid+dashed). G87 variants draw the same preview as the
// vanilla ones; callers add a "G87" text mark next to it.
export const isG87LineStyle = (style: number): boolean =>
  style === 2 || style === 3 || style === 6 || style === 7;

// Slightly brighter than YELLOW, so a 2px line still reads as yellow on the dark panel.
const PAINT_YELLOW = "#f5d05e";

export const LineStylePreview = ({
  style,
  width = 36,
  height = 10,
}: {
  style: number;
  width?: number;
  height?: number;
}) => {
  const dashed = style === 1 || style === 3 || style === 7;
  const dashedShort = style === 5;
  const dashedLong = style === 8 || style === 11;
  const double = style === 4 || style === 12;
  const border = style === 9;
  const solidDashed = style === 13;
  const paint =
    style === 6 || style === 7 || (style >= 10 && style <= 13) ? PAINT_YELLOW : PAINT;
  return (
    <svg width={width} height={height} viewBox="0 0 36 10" fill="none">
      {solidDashed ? (
        // US passing-zone centre line: solid on one side, dashed on the other.
        <>
          <rect x="1" y="2.5" width="34" height="2" fill={paint} />
          <rect x="1" y="5.5" width="8" height="2" fill={paint} />
          <rect x="14" y="5.5" width="8" height="2" fill={paint} />
          <rect x="27" y="5.5" width="8" height="2" fill={paint} />
        </>
      ) : border ? (
        // Curb: concrete band with a shadow edge, not paint.
        <>
          <rect x="1" y="3" width="34" height="4" rx="1" fill="#97a0a8" />
          <rect x="1" y="6" width="34" height="1" fill="#5a6068" />
        </>
      ) : double ? (
        <>
          <rect x="1" y="2.5" width="34" height="2" fill={paint} />
          <rect x="1" y="5.5" width="34" height="2" fill={paint} />
        </>
      ) : dashedLong ? (
        <>
          <rect x="1" y="4" width="13" height="2" fill={paint} />
          <rect x="18" y="4" width="13" height="2" fill={paint} />
          <rect x="35" y="4" width="1" height="2" fill={paint} />
        </>
      ) : dashedShort ? (
        <>
          <rect x="1" y="4" width="3" height="2" fill={paint} />
          <rect x="6.5" y="4" width="3" height="2" fill={paint} />
          <rect x="12" y="4" width="3" height="2" fill={paint} />
          <rect x="17.5" y="4" width="3" height="2" fill={paint} />
          <rect x="23" y="4" width="3" height="2" fill={paint} />
          <rect x="28.5" y="4" width="3" height="2" fill={paint} />
          <rect x="34" y="4" width="1" height="2" fill={paint} />
        </>
      ) : dashed ? (
        <>
          <rect x="1" y="4" width="6" height="2" fill={paint} />
          <rect x="10.5" y="4" width="6" height="2" fill={paint} />
          <rect x="20" y="4" width="6" height="2" fill={paint} />
          <rect x="29.5" y="4" width="5.5" height="2" fill={paint} />
        </>
      ) : (
        <rect x="1" y="4" width="34" height="2" fill={paint} />
      )}
    </svg>
  );
};

// Diagonal hatch lines across the square; the svg viewport clips the overhang. step sets
// the stripe density.
const hatch = (color: string, step: number, strokeWidth = 1.6) => {
  const lines = [];
  for (let x = -14 + step; x < 28; x += step) {
    lines.push(
      <line key={x} x1={x} y1={14} x2={x + 14} y2={0} stroke={color} strokeWidth={strokeWidth} />,
    );
  }
  return lines;
};

// Keyed by styleId (kStyleSurfaceNames and the areaStyle.* i18n keys): 0 Concrete,
// 1 Junction box (G87), 2 White stripes, 3 White stripes sparse, 4 Yellow stripes, 5 Green
// bike lane, 6 Red bus lane, 7-13 unused, 14 Asphalt (G87 VA), 15+ vanilla surfaces.
const SOLID_FILLS: Record<number, string> = {
  0: CONCRETE,
  5: BIKE_GREEN,
  6: BUS_RED,
  14: "#3f444a",  // asphalt
  15: "#4d7a3a",  // grass
  17: "#3d5c2e",  // grass, dark
  18: "#c9a961",  // sand
  19: "#8f8f89",  // pavement
  20: "#a39682",  // tiles 1
  21: "#93857a",  // tiles 2
  22: "#7e8291",  // tiles 3
};

export const AreaStylePreview = ({ styleId, size = 14 }: { styleId: number; size?: number }) => {
  const solidFill = SOLID_FILLS[styleId] ?? null;
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
