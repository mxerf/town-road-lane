// Base SVG icon. The colour is an explicit prop because cohtml resolves currentColor inside
// SVG to black regardless of the parent's CSS `color`.

import { SVGProps } from "react";

export interface IconProps extends Omit<SVGProps<SVGSVGElement>, "ref" | "color"> {
  size?: number | string;
  /** Stroke colour, any CSS colour string. Defaults to near-white for the dark panel. */
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
