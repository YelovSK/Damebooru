import { ChangeDetectionStrategy, Component, input, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost' | 'success' | 'warning';
export type ButtonSize = 'sm' | 'md' | 'lg' | 'xs' | 'icon';
export type ButtonType = 'button' | 'submit' | 'reset';

@Component({
  selector: 'app-button',
  standalone: true,
  imports: [CommonModule, RouterModule],
  templateUrl: './button.component.html',
  styleUrl: './button.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ButtonComponent {
  variant = input<ButtonVariant>('primary');
  size = input<ButtonSize>('md');
  type = input<ButtonType>('button');
  disabled = input<boolean>(false);
  loading = input<boolean>(false);
  icon = input<string | undefined>(undefined);
  fullWidth = input<boolean>(false);
  routerLink = input<(string | number)[] | string | null | undefined>(undefined);
  href = input<string | undefined>(undefined);
  target = input<string | undefined>(undefined);

  buttonClasses = computed(() => {
    const baseClasses = 'inline-flex items-center text-center justify-center font-semibold cursor-pointer transition-all duration-200 rounded-md border disabled:opacity-30 disabled:cursor-not-allowed disabled:transform-none w-full';
    
    const variantClasses: Record<ButtonVariant, string> = {
      primary: 'bg-accent-primary-glow text-accent-primary border-accent-primary/20 hover:bg-accent-primary hover:text-bg-primary hover:-translate-y-0.5 hover:shadow-[0_5px_15px_var(--color-accent-primary-glow)]',
      secondary: 'bg-glass-bg text-text-muted border-glass-border hover:bg-glass-bg-hover hover:text-text-primary hover:-translate-y-0.5',
      danger: 'bg-transparent border-status-error/50 text-status-error hover:bg-status-error hover:text-white hover:shadow-[0_0_15px_rgba(239,68,68,0.2)]',
      ghost: 'bg-transparent border-transparent text-text-dim hover:text-status-error',
      success: 'bg-transparent border-status-success/50 text-status-success hover:bg-status-success hover:text-white',
      warning: 'bg-transparent border-status-warning/50 text-status-warning hover:bg-status-warning hover:text-white'
    };

    const sizeClasses: Record<ButtonSize, string> = {
      xs: 'px-2 py-0.5 text-[0.65rem]',
      sm: 'px-3 py-1.5 text-[0.75rem]',
      md: 'px-4 py-2 text-sm',
      lg: 'px-6 py-3 text-base',
      icon: 'p-1 aspect-square min-w-[2rem] text-lg'
    };

    const paddingClasses = this.size() === 'icon' ? '' : (this.variant() === 'ghost' && this.size() === 'md' ? 'p-1' : '');

    return `${baseClasses} ${variantClasses[this.variant()]} ${sizeClasses[this.size()]} ${this.fullWidth() ? 'w-full' : ''} ${paddingClasses}`;
  });
}
