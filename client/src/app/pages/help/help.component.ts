import { ChangeDetectionStrategy, Component } from '@angular/core';
import { CommonModule } from '@angular/common';

type ShortcutItem = {
  key: string;
  context: string;
  action: string;
};

type SearchSyntaxItem = {
  syntax: string;
  description: string;
  example: string;
};

@Component({
  selector: 'app-help',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="max-w-6xl mx-auto py-8 space-y-8">
      <section class="glass-card p-6">
        <h1 class="text-3xl font-bold text-accent-primary">Help</h1>
        <p class="text-text-dim mt-2">
          Keyboard shortcuts, search syntax, and quick examples.
        </p>
      </section>

      <section class="glass-card p-6">
        <h2 class="text-xl font-semibold mb-4">Keyboard Shortcuts</h2>
        <div class="overflow-x-auto">
          <table class="w-full text-left">
            <thead>
              <tr class="border-b border-glass-border">
                <th class="py-2 pr-4 text-text-dim font-medium">Key</th>
                <th class="py-2 pr-4 text-text-dim font-medium">Context</th>
                <th class="py-2 text-text-dim font-medium">Action</th>
              </tr>
            </thead>
            <tbody>
              <tr *ngFor="let item of shortcuts" class="border-b border-white/5">
                <td class="py-2 pr-4"><code>{{ item.key }}</code></td>
                <td class="py-2 pr-4">{{ item.context }}</td>
                <td class="py-2">{{ item.action }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p class="text-sm text-text-dim mt-3">
          Global hotkeys are ignored while typing in input/textarea/select fields.
        </p>
      </section>

      <section class="glass-card p-6">
        <h2 class="text-xl font-semibold mb-4">Search Syntax</h2>
        <div class="overflow-x-auto">
          <table class="w-full text-left">
            <thead>
              <tr class="border-b border-glass-border">
                <th class="py-2 pr-4 text-text-dim font-medium">Syntax</th>
                <th class="py-2 pr-4 text-text-dim font-medium">Description</th>
                <th class="py-2 text-text-dim font-medium">Example</th>
              </tr>
            </thead>
            <tbody>
              <tr *ngFor="let item of searchSyntax" class="border-b border-white/5 align-top">
                <td class="py-2 pr-4"><code>{{ item.syntax }}</code></td>
                <td class="py-2 pr-4">{{ item.description }}</td>
                <td class="py-2"><code>{{ item.example }}</code></td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="glass-card p-6">
        <h2 class="text-xl font-semibold mb-4">Query Example</h2>
        <p class="text-text-dim">
          <code>artist_name -nsfw type:image,gif tag-count:&gt;3 filename:*wallpaper* sort:import-date:desc</code>
        </p>
      </section>
    </div>
  `,
})
export class HelpComponent {
  shortcuts: ShortcutItem[] = [
    { key: 'ArrowLeft', context: 'Posts page', action: 'Previous page' },
    { key: 'ArrowRight', context: 'Posts page', action: 'Next page' },
    { key: 'ArrowLeft', context: 'Post details', action: 'Go to previous post in current query' },
    { key: 'ArrowRight', context: 'Post details', action: 'Go to next post in current query' },
    { key: 'E', context: 'Post details', action: 'Toggle edit mode' },
  ];

  searchSyntax: SearchSyntaxItem[] = [
    { syntax: 'tag_name', description: 'Include tag', example: 'landscape' },
    { syntax: '-tag_name', description: 'Exclude tag', example: '-nsfw' },
    { syntax: 'type:image,gif,video', description: 'Filter by media type', example: 'type:video' },
    { syntax: '-type:image,gif,video', description: 'Exclude media type', example: '-type:video' },
    { syntax: 'tag-count:[op]N', description: 'Filter by number of tags, operators: =, >, >=, <, <=', example: 'tag-count:>=5' },
    { syntax: 'favorite:true|false', description: 'Filter favorite posts', example: 'favorite:true' },
    { syntax: 'filename:TEXT', description: 'Match text in relative file path', example: 'filename:abc.jpg' },
    { syntax: 'filename:*pattern*', description: 'Filename wildcard search (* and ?)', example: 'filename:*wallpaper*' },
    { syntax: '-filename:TEXT', description: 'Exclude matching file paths', example: '-filename:tmp' },
    { syntax: 'sort:FIELD', description: 'Sort ascending by default', example: 'sort:id' },
    { syntax: 'sort:FIELD:asc|desc', description: 'Explicit sort direction', example: 'sort:tag-count:desc' },
    { syntax: 'sort:new / sort:old', description: 'Date aliases (newest/oldest)', example: 'sort:new' },
  ];
}
