import { IconBase, IconProps } from "./Icon";

export const Eye = (props: IconProps) => (
  <IconBase {...props}>
    <path d="M1.5 8 C 3 4, 5.5 2.5, 8 2.5 C 10.5 2.5, 13 4, 14.5 8 C 13 12, 10.5 13.5, 8 13.5 C 5.5 13.5, 3 12, 1.5 8 Z" />
    <circle cx="8" cy="8" r="2" />
  </IconBase>
);

export const EyeOff = (props: IconProps) => (
  <IconBase {...props}>
    <path d="M1.5 8 C 3 4, 5.5 2.5, 8 2.5 C 10.5 2.5, 13 4, 14.5 8 C 13 12, 10.5 13.5, 8 13.5 C 5.5 13.5, 3 12, 1.5 8 Z" />
    <circle cx="8" cy="8" r="2" />
    <line x1="2" y1="14" x2="14" y2="2" />
  </IconBase>
);
