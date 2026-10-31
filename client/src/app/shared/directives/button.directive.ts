import { booleanAttribute, computed, Directive, input } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost' | 'success' | 'warning';
export type ButtonSize = 'sm' | 'md' | 'lg' | 'icon';

const baseClasses = 'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-lg border font-medium cursor-pointer transition-colors outline-accent focus-visible:outline-2 focus-visible:outline-offset-2 disabled:opacity-40 disabled:cursor-not-allowed';

const variantClasses: Record<ButtonVariant, string> = {
  primary: 'bg-accent border-accent text-on-accent hover:brightness-110',
  secondary: 'bg-raised border-line-strong text-fg hover:bg-line-strong',
  ghost: 'bg-transparent border-transparent text-fg-muted hover:bg-raised hover:text-fg',
  danger: 'bg-transparent border-danger/40 text-danger hover:bg-danger/15 hover:border-danger/60',
  success: 'bg-transparent border-success/40 text-success hover:bg-success/15 hover:border-success/60',
  warning: 'bg-transparent border-warning/40 text-warning hover:bg-warning/15 hover:border-warning/60',
};

const sizeClasses: Record<ButtonSize, string> = {
  sm: 'h-8 px-3 text-[0.8125rem]',
  md: 'h-10 px-4 text-sm',
  lg: 'h-11 px-6 text-base',
  icon: 'h-8 w-8 text-base',
};

@Directive({
  selector: 'button[appButton], a[appButton]',
  standalone: true,
  host: { '[class]': 'classes()' },
})
export class ButtonDirective {
  readonly variant = input<ButtonVariant>('secondary');
  readonly size = input<ButtonSize>('md');
  readonly fullWidth = input(false, { transform: booleanAttribute });

  protected readonly classes = computed(() => [
    baseClasses,
    variantClasses[this.variant()],
    sizeClasses[this.size()],
    this.fullWidth() ? 'w-full' : '',
  ].join(' '));
}
