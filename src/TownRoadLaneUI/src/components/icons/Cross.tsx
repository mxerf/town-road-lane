import { IconBase, IconProps } from "./Icon";

export const Cross = (props: IconProps) => (
  <IconBase {...props}>
    <line x1="4" y1="4" x2="12" y2="12" />
    <line x1="12" y1="4" x2="4" y2="12" />
  </IconBase>
);
