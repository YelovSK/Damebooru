import { booleanAttribute, computed, Directive, effect, inject, input, Renderer2, ElementRef } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost' | 'success' | 'warning';
export type ButtonSize = 'xs' | 'sm' | 'md' | 'lg' | 'icon';

interface ButtonClassOptions {
  variant: ButtonVariant;
  size: ButtonSize;
  fullWidth?: boolean;
}

const baseClasses = 'inline-flex items-center text-center justify-center font-semibold cursor-pointer transition-all duration-200 rounded-md border disabled:opacity-30 disabled:cursor-not-allowed disabled:transform-none';

const variantClasses: Record<ButtonVariant, string> = {
  primary: 'bg-accent/15 text-accent border-accent/20 hover:bg-accent hover:text-on-accent hover:-translate-y-0.5',
  secondary: 'bg-raised text-fg border-line-strong hover:bg-line-strong hover:-translate-y-0.5',
  danger: 'bg-transparent border-danger/50 text-danger hover:bg-danger hover:text-on-accent',
  ghost: 'bg-transparent border-transparent text-fg-subtle hover:text-danger',
  success: 'bg-transparent border-success/50 text-success hover:bg-success hover:text-on-accent',
  warning: 'bg-transparent border-warning/50 text-warning hover:bg-warning hover:text-on-accent',
};

const sizeClasses: Record<ButtonSize, string> = {
  xs: 'px-2 py-0.5 text-[0.65rem]',
  sm: 'px-3 py-1.5 text-[0.75rem]',
  md: 'px-4 py-2 text-sm',
  lg: 'px-6 py-3 text-base',
  icon: 'p-1 aspect-square min-w-[2rem] text-lg',
};

function buildButtonClasses(options: ButtonClassOptions): string {
  const paddingClasses = options.size === 'icon'
    ? ''
    : options.variant === 'ghost' && options.size === 'md'
      ? 'p-1'
      : '';

  return [
    baseClasses,
    variantClasses[options.variant],
    sizeClasses[options.size],
    options.fullWidth ? 'w-full' : '',
    paddingClasses,
  ].filter(Boolean).join(' ');
}

@Directive({
  selector: 'button[appButton], a[appButton]',
  standalone: true,
})
export class ButtonDirective {
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly renderer = inject(Renderer2);
  private readonly appliedClasses = new Set<string>();

  readonly variant = input<ButtonVariant>('primary');
  readonly size = input<ButtonSize>('md');
  readonly fullWidth = input(false, { transform: booleanAttribute });

  private readonly buttonClasses = computed(() => buildButtonClasses({
    variant: this.variant(),
    size: this.size(),
    fullWidth: this.fullWidth(),
  }));

  constructor() {
    effect(() => {
      this.syncClasses(this.buttonClasses());
    });
  }

  private syncClasses(classList: string): void {
    const nextClasses = new Set(classList.split(/\s+/).filter(Boolean));
    const element = this.elementRef.nativeElement;

    for (const className of this.appliedClasses) {
      if (!nextClasses.has(className)) {
        this.renderer.removeClass(element, className);
      }
    }

    for (const className of nextClasses) {
      if (!this.appliedClasses.has(className)) {
        this.renderer.addClass(element, className);
      }
    }

    this.appliedClasses.clear();
    for (const className of nextClasses) {
      this.appliedClasses.add(className);
    }
  }
}
