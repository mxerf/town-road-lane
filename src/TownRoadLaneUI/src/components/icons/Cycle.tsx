import { IconBase, IconProps } from "./Icon";

// Cycle-through-options icon — used on the segment popover style button.
// Visualizes "switch to next variant" — circular arrow loop.
export const Cycle = (props: IconProps) => (
  <IconBase {...props}>
    <path d="M3 8 a 5 5 0 0 1 8.5 -3.5" />
    <polyline points="11.5,2 11.5,4.5 9,4.5" />
    <path d="M13 8 a 5 5 0 0 1 -8.5 3.5" />
    <polyline points="4.5,14 4.5,11.5 7,11.5" />
  </IconBase>
);
