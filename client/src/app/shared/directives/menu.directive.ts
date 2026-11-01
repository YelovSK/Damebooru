import { Directive } from '@angular/core';
import { CdkMenu, CdkMenuItem } from '@angular/cdk/menu';

@Directive({
  selector: '[appMenu]',
  standalone: true,
  hostDirectives: [CdkMenu],
  host: { class: 'flex flex-col gap-0.5 rounded-lg border border-line-strong bg-surface p-1 text-fg shadow-xl' },
})
export class MenuDirective {}

/** Items inherit the menu's text color, so a color class on the item (e.g. `text-danger`) overrides it. */
@Directive({
  selector: '[appMenuItem]',
  standalone: true,
  hostDirectives: [{
    directive: CdkMenuItem,
    inputs: ['cdkMenuItemDisabled'],
    outputs: ['cdkMenuItemTriggered'],
  }],
  host: {
    class: 'w-full cursor-pointer rounded-md px-3 py-1.5 text-left text-sm outline-none hover:bg-raised focus:bg-raised '
      + 'aria-disabled:cursor-not-allowed aria-disabled:bg-transparent aria-disabled:opacity-40',
  },
})
export class MenuItemDirective {}
