import { ChangeDetectionStrategy, Component } from '@angular/core';
import { CommonModule } from '@angular/common';

interface ShortcutItem {
  key: string;
  context: string;
  action: string;
}

interface SearchSyntaxItem {
  syntax: string;
  description: string;
  example: string;
}

interface SortFieldItem {
  field: string;
  aliases?: string[];
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
    { syntax: 'tag_name', description: 'Include tag', example: 'landscape' },
    { syntax: '-tag_name', description: 'Exclude tag', example: '-nsfw' },
    { syntax: 'type:image,gif,video', description: 'Filter by media type', example: 'type:video' },
    { syntax: '-type:image,gif,video', description: 'Exclude media type', example: '-type:video' },
    { syntax: 'tag-count:[op]N', description: 'Filter by number of tags, operators: =, >, >=, <, <=', example: 'tag-count:>=5' },
    { syntax: 'favorite:true|false', description: 'Filter favorite posts', example: 'favorite:true' },
    { syntax: 'filename:TEXT', description: 'Match text in relative file path', example: 'filename:abc.jpg' },
    { syntax: 'filename:*pattern*', description: 'Filename wildcard search (* and ?)', example: 'filename:*wallpaper*' },
    { syntax: '-filename:TEXT', description: 'Exclude matching file paths', example: '-filename:tmp' },
    { syntax: 'sort:FIELD', description: 'Sort by field (asc by default)', example: 'sort:id' },
    { syntax: 'sort:FIELD:asc|desc', description: 'Explicit sort direction', example: 'sort:tag-count:desc' },
    { syntax: 'sort:new / sort:old', description: 'Aliases for file modified date with direction presets (new=desc, old=asc)', example: 'sort:new' },
  ];

  sortFields: SortFieldItem[] = [
    { field: 'id' },
    { field: 'modified-date', aliases: ['date', 'file-date', 'file-modified-date'] },
    { field: 'import-date' },
    { field: 'tag-count', aliases: ['tagcount', 'tags'] },
    { field: 'width' },
    { field: 'height' },
    { field: 'size', aliases: ['size-bytes', 'filesize'] },
  ];
}
