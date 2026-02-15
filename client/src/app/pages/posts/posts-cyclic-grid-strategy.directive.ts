import { Directive, Input, OnChanges, SimpleChanges, forwardRef } from '@angular/core';
import { VIRTUAL_SCROLL_STRATEGY } from '@angular/cdk/scrolling';

import { PostsCyclicGridStrategy } from './posts-cyclic-grid-strategy';

function postsCyclicGridStrategyFactory(
    directive: PostsCyclicGridStrategyDirective
): PostsCyclicGridStrategy {
    return directive.strategy;
}

@Directive({
    selector: 'cdk-virtual-scroll-viewport[appPostsCyclicGridStrategy]',
    standalone: true,
    providers: [
        {
            provide: VIRTUAL_SCROLL_STRATEGY,
            useFactory: postsCyclicGridStrategyFactory,
            deps: [forwardRef(() => PostsCyclicGridStrategyDirective)],
        },
    ],
})
export class PostsCyclicGridStrategyDirective implements OnChanges {
    private readonly strategyImpl = new PostsCyclicGridStrategy();

    @Input() appPostsCyclicGridStrategyPostRowHeightPx = 1;
    @Input() appPostsCyclicGridStrategySeparatorRowHeightPx = 1;
    @Input() appPostsCyclicGridStrategyRowCount = 0;
    @Input() appPostsCyclicGridStrategyTotalCount: number | null = null;
    @Input() appPostsCyclicGridStrategyPageSize = 100;
    @Input() appPostsCyclicGridStrategyColumns = 1;
    @Input() appPostsCyclicGridStrategyAnchorPageHint = 1;
    @Input() appPostsCyclicGridStrategyMinBufferRows = 12;
    @Input() appPostsCyclicGridStrategyMaxBufferRows = 24;

    get strategy(): PostsCyclicGridStrategy {
        return this.strategyImpl;
    }

    ngOnChanges(_changes: SimpleChanges): void {
        this.strategyImpl.updateConfig({
            postRowHeightPx: this.appPostsCyclicGridStrategyPostRowHeightPx,
            separatorRowHeightPx: this.appPostsCyclicGridStrategySeparatorRowHeightPx,
            rowCount: this.appPostsCyclicGridStrategyRowCount,
            totalCount: this.appPostsCyclicGridStrategyTotalCount,
            pageSize: this.appPostsCyclicGridStrategyPageSize,
            columns: this.appPostsCyclicGridStrategyColumns,
            anchorPageHint: this.appPostsCyclicGridStrategyAnchorPageHint,
            minBufferRows: this.appPostsCyclicGridStrategyMinBufferRows,
            maxBufferRows: this.appPostsCyclicGridStrategyMaxBufferRows,
        });
    }
}
