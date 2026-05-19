// Base SVG icon wrapper. cohtml does not reliably propagate `color` from a
// parent element down into SVG children (currentColor resolves to black
// regardless of CSS `color`), so we pass an explicit `color` prop and use it
// directly on stroke/fill. Default is white at 90% alpha — readable on every
// dark surface in the panel. Callers wanting a tint pass `color="..."`.

import { SVGProps } from "react";

export interface IconProps extends Omit<SVGProps<SVGSVGElement>, "ref" | "color"> {
  size?: number | string;
  /** Explicit colour for stroke/fill. Defaults to a near-white tint suitable
   * for dark panel surfaces. Pass any CSS colour string. */
  color?: string;
  title?: string;
}

interface BaseProps extends IconProps {
  children: React.ReactNode;
}

const DEFAULT_COLOR = "rgba(232, 234, 237, 0.92)";

export const IconBase = ({
  size = 14,
  color = DEFAULT_COLOR,
  title,
  children,
  ...rest
}: BaseProps) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 16 16"
    fill="none"
    stroke={color}
    strokeWidth={1.5}
    strokeLinecap="round"
    strokeLinejoin="round"
    role={title ? "img" : "presentation"}
    aria-label={title}
    {...rest}
  >
    {title && <title>{title}</title>}
    {children}
  </svg>
);
