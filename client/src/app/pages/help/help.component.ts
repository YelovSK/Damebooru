import { ChangeDetectionStrategy, Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  SEARCH_DIRECTIVES,
  SORT_DIRECTIVE,
  formatSearchDirectiveSyntax,
  type SearchDirective,
} from '@shared/utils/post-search-syntax';

interface ShortcutItem {
  key: string;
  context: string;
  action: string;
}

interface SearchSyntaxItem {
  syntax: string;
  aliases: readonly string[];
  description: string;
  examples: readonly string[];
}

@Component({
  selector: 'app-help',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './help.component.html',
})
export class HelpComponent {
  shortcuts: ShortcutItem[] = [
    { key: 'ArrowLeft', context: 'Posts page', action: 'Previous page' },
    { key: 'ArrowRight', context: 'Posts page', action: 'Next page' },
    { key: 'ArrowLeft', context: 'Post details', action: 'Go to previous post in current query' },
    { key: 'ArrowRight', context: 'Post details', action: 'Go to next post in current query' },
    { key: 'E', context: 'Post details', action: 'Toggle edit mode' },
    { key: 'F', context: 'Post details', action: 'Toggle fullscreen media view' },
  ];

  searchSyntax: SearchSyntaxItem[] = [
    { syntax: 'tag_name', aliases: [], description: 'Include tag', examples: ['landscape'] },
    { syntax: '-tag_name', aliases: [], description: 'Exclude tag', examples: ['-nsfw'] },
    ...SEARCH_DIRECTIVES.map(toSyntaxItem),
  ];

  sortOptions = [...SORT_DIRECTIVE.value.presets, ...SORT_DIRECTIVE.value.fields];
}

function toSyntaxItem(directive: SearchDirective): SearchSyntaxItem {
  return {
    syntax: formatSearchDirectiveSyntax(directive),
    aliases: directive.aliases,
    description: directive.negatable ? `${directive.description} Prefix with - to exclude.` : directive.description,
    examples: directive.examples,
  };
}
