import { IconBase, IconProps } from "./Icon";

// Points right; rotate 90deg with a CSS transform for an open accordion.
export const ChevronRight = (props: IconProps) => (
  <IconBase {...props}>
    <polyline points="6,3 11,8 6,13" />
  </IconBase>
);
