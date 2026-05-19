import { IconBase, IconProps } from "./Icon";

// Chevron pointing right by default; rotate via CSS transform for accordion
// open state (90deg = points down). Used in line-row accordion headers.
export const ChevronRight = (props: IconProps) => (
  <IconBase {...props}>
    <polyline points="6,3 11,8 6,13" />
  </IconBase>
);
