import { IconBase, IconProps } from "./Icon";

export const Trash = (props: IconProps) => (
  <IconBase {...props}>
    <polyline points="2.5,4 13.5,4" />
    <path d="M5 4 V 2.5 a 1 1 0 0 1 1 -1 h 4 a 1 1 0 0 1 1 1 V 4" />
    <path d="M3.5 4 l 0.7 9 a 1 1 0 0 0 1 1 h 5.6 a 1 1 0 0 0 1 -1 l 0.7 -9" />
    <line x1="6.5" y1="7" x2="6.5" y2="11.5" />
    <line x1="9.5" y1="7" x2="9.5" y2="11.5" />
  </IconBase>
);
