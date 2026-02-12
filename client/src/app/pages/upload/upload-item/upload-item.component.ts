import {
  Component,
  inject,
  input,
  signal,
  computed,
  ChangeDetectionStrategy,
  effect,
  DestroyRef,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subject, switchMap, of, map, catchError } from 'rxjs';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { Tag } from '@models';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { UploadService } from '@services/upload.service';
import { RateLimiterService } from '@services/rate-limiting/rate-limiter.service';
import { AutocompleteComponent } from '@shared/components/autocomplete/autocomplete.component';
import { ButtonComponent } from '@shared/components/button/button.component';
import { AutoTaggingResultsComponent } from '@shared/components/auto-tagging-results/auto-tagging-results.component';
import { ProgressBarComponent } from '@shared/components/progress-bar/progress-bar.component';
import { escapeTagName } from '@shared/utils/utils';
import { AppLinks } from '@app/app.paths';

@Component({
  selector: 'app-upload-item',
  standalone: true,
  imports: [CommonModule, AutocompleteComponent, RouterLink, ButtonComponent, AutoTaggingResultsComponent, ProgressBarComponent],
  templateUrl: './upload-item.component.html',
  styleUrl: './upload-item.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UploadItemComponent {
  private readonly uploadService = inject(UploadService);
  private readonly bakabooru = inject(BakabooruService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly rateLimiter = inject(RateLimiterService);

  readonly appLinks = AppLinks;
  readonly rateLimiterStatuses = this.rateLimiter.statuses;

  itemId = input.required<string>();
  registeredProviders = computed(() =>
    this.uploadService.getRegisteredProviders(),
  );

  item = computed(() =>
    this.uploadService.uploadQueue().find((i) => i.id === this.itemId()),
  );

  // Tag autocomplete
  private tagQuery$ = new Subject<string>();
  tagSuggestions = toSignal(
    this.tagQuery$.pipe(
      switchMap((word) => {
        if (word.length < 1) return of([]);
        return this.bakabooru.getTags(`*${word}* sort:usages`, 0, 10).pipe(
          map((res) => res.results),
          catchError(() => of([])),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ),
    { initialValue: [] as Tag[] },
  );
  currentSearchValue = signal('');
  currentSourcesValue = signal('');

  constructor() {
    // When currentSearchValue changes, parse tags and update the queue
    effect(() => {
      const tags = this.parseTags(this.currentSearchValue());
      const currentItem = this.item();
      if (
        currentItem &&
        JSON.stringify(tags) !== JSON.stringify(currentItem.tags)
      ) {
        this.uploadService.updateItem({ ...currentItem, tags });
      }
    });

    // When currentSourcesValue changes, parse sources and update the queue
    effect(() => {
      const sources = this.parseSources(this.currentSourcesValue());
      const currentItem = this.item();
      if (
        currentItem &&
        JSON.stringify(sources) !== JSON.stringify(currentItem.sources)
      ) {
        this.uploadService.setSources(this.itemId(), sources);
      }
    });

    // Initialize currentSearchValue from existing tags
    effect(() => {
      const currentItem = this.item();
      if (currentItem && this.currentSearchValue() === '') {
        this.currentSearchValue.set(currentItem.tags.join(' ') + ' ');
      }
    });

    // Initialize currentSourcesValue from existing sources
    effect(() => {
      const currentItem = this.item();
      if (currentItem && this.currentSourcesValue() === '' && currentItem.sources.length > 0) {
        this.currentSourcesValue.set(currentItem.sources.join('\n'));
      }
    });
  }

  private parseTags(text: string): string[] {
    return text
      .trim()
      .split(/\s+/)
      .filter((t: string) => t.length > 0);
  }

  private parseSources(text: string): string[] {
    return text
      .split('\n')
      .map((s) => s.trim())
      .filter((s) => s.length > 0);
  }

  onTagQueryChange(word: string) {
    this.tagQuery$.next(escapeTagName(word));
  }

  onTagSelection(tag: Tag) {
    const value = this.currentSearchValue().trimEnd();
    const parts = value.split(/\s+/);
    parts[parts.length - 1] = escapeTagName(tag.names[0]);
    const newValue = parts.join(' ') + ' ';

    this.currentSearchValue.set(newValue);
    this.tagQuery$.next('');
  }

  onSearch(tagsString: string) {
    // Triggered on Enter - just parse and store
    const tags = this.parseTags(tagsString);
    const currentItem = this.item();
    if (currentItem) {
      this.uploadService.updateItem({ ...currentItem, tags });
    }
  }

  startUpload() {
    this.uploadService.startUpload(this.itemId());
  }

  retry() {
    this.uploadService.retry(this.itemId());
  }

  cancel() {
    this.uploadService.cancel(this.itemId());
  }

  removeMe() {
    this.cancel();
    this.uploadService.removeItem(this.itemId());
  }

  setSafety(safety: string) {
    this.uploadService.setSafety(
      this.itemId(),
      safety as 'safe' | 'sketchy' | 'unsafe',
    );
  }

  applyAutoTags(providerId: string) {
    this.uploadService.applyAutoTags(this.itemId(), providerId);

    // Refresh the input field from the updated item tags
    const currentItem = this.item();
    if (currentItem) {
      this.currentSearchValue.set(currentItem.tags.join(' ') + ' ');
    }
  }

  applyAutoSources(providerId: string) {
    this.uploadService.applyAutoSources(this.itemId(), providerId);

    // Refresh the input field from the updated item sources
    const currentItem = this.item();
    if (currentItem) {
      this.currentSourcesValue.set(currentItem.sources.join('\n'));
    }
  }

  applyAutoSafety(providerId: string) {
    this.uploadService.applyAutoSafety(this.itemId(), providerId);
  }

  onSourcesChange(event: Event) {
    const value = (event.target as HTMLTextAreaElement).value;
    this.currentSourcesValue.set(value);
  }

  triggerAutoTagging() {
    this.uploadService.triggerAutoTagging(this.itemId());
  }

  triggerProviderAutoTagging(providerId: string) {
    this.uploadService.triggerProviderAutoTagging(this.itemId(), providerId);
  }
}
