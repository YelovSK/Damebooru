import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { switchMap } from 'rxjs';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';

import { DamebooruService } from '@app/services/api/damebooru/damebooru.service';
import { StatsGrowthDateKind, StatsSeriesPointDto, StatsStorageBreakdownDto, StatsTagCategoryDto, StatsTagDensityBucketDto, TagCategoryKind } from '@app/services/api/damebooru/models';
import { CommonModule, DecimalPipe, DatePipe } from '@angular/common';
import { FileSizePipe } from '@app/shared/pipes/file-size.pipe';
import { TabComponent } from '@app/shared/components/tabs/tab.component';
import { TabsComponent } from '@app/shared/components/tabs/tabs.component';
import { LineChartComponent, LineChartPoint } from '@app/shared/components/line-chart/line-chart.component';
import { formatBytes } from '@app/shared/utils/utils';
import { ButtonDirective } from '@app/shared/directives/button.directive';

@Component({
    selector: 'app-stats',
    standalone: true,
    imports: [CommonModule, DecimalPipe, DatePipe, FileSizePipe, TabsComponent, TabComponent, LineChartComponent, ButtonDirective],
    templateUrl: './stats.component.html',
    styleUrl: './stats.component.css',
    changeDetection: ChangeDetectionStrategy.OnPush
})
export class StatsComponent {
    private readonly damebooru = inject(DamebooruService);

    readonly growthDateKind = signal<StatsGrowthDateKind>('Imported');
    readonly overview = toSignal(this.damebooru.getStatsOverview(), { initialValue: null });
    readonly storage = toSignal(this.damebooru.getStatsStorage(), { initialValue: null });
    readonly tagStats = toSignal(this.damebooru.getStatsTags(), { initialValue: null });
    readonly maintenance = toSignal(this.damebooru.getStatsMaintenance(), { initialValue: null });
    readonly growth = toSignal(
        toObservable(this.growthDateKind).pipe(
            switchMap(dateKind => this.damebooru.getStatsGrowth(dateKind))
        ),
        { initialValue: null }
    );

    readonly cumulativePostPoints = computed(() => this.toChartPoints(this.growth()?.cumulativePosts ?? []));
    readonly cumulativeSizePoints = computed(() => this.toChartPoints(this.growth()?.cumulativeSizeBytes ?? []));
    readonly growthDateDescription = computed(() => this.growthDateKind() === 'Imported'
        ? 'Charts are grouped by when posts were imported into Damebooru.'
        : 'Charts are grouped by stored filesystem modified dates.');

    readonly formatCount = (value: number): string => Math.round(value).toLocaleString();
    readonly formatSize = (value: number): string => formatBytes(value, 1);

    storageSizePercent(item: StatsStorageBreakdownDto): number {
        const total = this.storage()?.totalSizeBytes ?? 0;
        return total <= 0 ? 0 : (item.sizeBytes / total) * 100;
    }

    formatPercent(value: number): string {
        if (!Number.isFinite(value) || value <= 0) {
            return '0%';
        }

        return `${value.toFixed(value < 1 ? 1 : 0)}%`;
    }

    tagCategoryLabel(category: TagCategoryKind): string {
        switch (category) {
            case TagCategoryKind.Artist:
                return 'Artist';
            case TagCategoryKind.Character:
                return 'Character';
            case TagCategoryKind.Copyright:
                return 'Copyright';
            case TagCategoryKind.Meta:
                return 'Meta';
            case TagCategoryKind.General:
            default:
                return 'General';
        }
    }

    tagCategoryPercent(item: StatsTagCategoryDto): number {
        const total = this.tagStats()?.categories.reduce((sum, category) => sum + category.postCount, 0) ?? 0;
        return total <= 0 ? 0 : (item.postCount / total) * 100;
    }

    tagDensityPercent(item: StatsTagDensityBucketDto): number {
        const total = this.overview()?.postCount ?? 0;
        return total <= 0 ? 0 : (item.postCount / total) * 100;
    }

    setGrowthDateKind(dateKind: StatsGrowthDateKind): void {
        this.growthDateKind.set(dateKind);
    }

    private toChartPoints(points: readonly StatsSeriesPointDto[]): LineChartPoint[] {
        return points.map(point => ({
            label: point.label,
            value: point.value,
        }));
    }
}
