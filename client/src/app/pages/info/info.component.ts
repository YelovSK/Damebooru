import { Component, inject, signal, OnInit } from '@angular/core';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { GlobalInfo } from '@services/api/oxibooru/models';
import { CommonModule, DecimalPipe, DatePipe } from '@angular/common';

@Component({
    selector: 'app-info',
    standalone: true,
    imports: [CommonModule, DecimalPipe, DatePipe],
    templateUrl: './info.component.html',
    styleUrl: './info.component.css'
})
export class InfoComponent implements OnInit {
    private readonly bakabooru = inject(BakabooruService);

    info = signal<GlobalInfo | null>(null);

    ngOnInit() {
        this.bakabooru.getGlobalInfo().subscribe(info => {
            this.info.set(info);
        });
    }

    formatSize(bytes: number): string {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }
}
