import { ChangeDetectionStrategy, Component, inject, signal, OnInit } from '@angular/core';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { DamebooruSystemInfoDto } from '@services/api/damebooru/models';
import { CommonModule, DecimalPipe, DatePipe } from '@angular/common';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';

@Component({
    selector: 'app-info',
    standalone: true,
    imports: [CommonModule, DecimalPipe, DatePipe, FileSizePipe],
    templateUrl: './info.component.html',
    styleUrl: './info.component.css',
    changeDetection: ChangeDetectionStrategy.OnPush
})
export class InfoComponent implements OnInit {
    private readonly damebooru = inject(DamebooruService);

    info = signal<DamebooruSystemInfoDto | null>(null);

    ngOnInit() {
        this.damebooru.getGlobalInfo().subscribe(info => {
            this.info.set(info);
        });
    }
}
