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
  primary: 'bg-accent-primary-glow text-accent-primary border-accent-primary/20 hover:bg-accent-primary hover:text-bg-primary hover:-translate-y-0.5 hover:shadow-[0_5px_15px_var(--color-accent-primary-glow)]',
  secondary: 'bg-[#1e3a5f] text-text-primary border-[#2a4d6e] hover:bg-[#264a6e] hover:text-white hover:-translate-y-0.5',
  danger: 'bg-transparent border-status-error/50 text-status-error hover:bg-status-error hover:text-white hover:shadow-[0_0_15px_rgba(239,68,68,0.2)]',
  ghost: 'bg-transparent border-transparent text-text-dim hover:text-status-error',
  success: 'bg-transparent border-status-success/50 text-status-success hover:bg-status-success hover:text-white',
  warning: 'bg-transparent border-status-warning/50 text-status-warning hover:bg-status-warning hover:text-white',
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
