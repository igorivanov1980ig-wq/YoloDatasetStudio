"""Local Ultralytics worker used by the WPF application.
The executable can be bundled with PyInstaller together with this module.
"""
import argparse
import gc
import json
import os
import sys
from pathlib import Path


def emit(payload):
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def load_ultralytics():
    try:
        from ultralytics import YOLO
        return YOLO
    except ImportError as exc:
        raise RuntimeError("Не установлена библиотека ultralytics. Выполните install.ps1.") from exc


def resolve_device(value):
    if str(value).lower() != "auto":
        return value
    try:
        import torch
        return "0" if torch.cuda.is_available() else "cpu"
    except Exception:
        return "cpu"


def annotate(request):
    YOLO = load_ultralytics()
    model = YOLO(request["model"])
    source = Path(request["source"])
    output = Path(request["output"])
    output.mkdir(parents=True, exist_ok=True)
    image_files = [p for p in source.rglob("*") if p.suffix.lower() in {".jpg", ".jpeg", ".png", ".bmp", ".webp"}]
    if not image_files:
        return {"ok": True, "processed": 0}
    mode = request.get("mode", "detect")
    threshold = float(request.get("confidence", 0.25))
    device = resolve_device(request.get("device", "cpu"))
    image_size = int(request.get("image_size", 640))
    count = 0
    total = len(image_files)
    try:
        for image_file in image_files:
            results = model.predict(source=str(image_file), conf=threshold, imgsz=image_size, batch=1, device=device, stream=False, verbose=False)
            for result in results:
                image_path = Path(result.path)
                try:
                    relative = image_path.resolve().relative_to(source.resolve())
                    target = output / relative.parent / (image_path.stem + ".txt")
                except ValueError:
                    target = output / (image_path.stem + ".txt")
                target.parent.mkdir(parents=True, exist_ok=True)
                lines = []
                boxes = result.boxes
                if boxes is not None:
                    for i, box in enumerate(boxes):
                        cls = int(box.cls[0].item())
                        xywh = box.xywhn[0].tolist()
                        if mode == "segment" and result.masks is not None and i < len(result.masks.xyn):
                            polygon = [str(cls)] + [str(round(v, 7)) for point in result.masks.xyn[i].tolist() for v in point]
                            lines.append(" ".join(polygon))
                        else:
                            lines.append(" ".join([str(cls)] + [f"{v:.7f}" for v in xywh]))
                target.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
                count += 1
            del results
            gc.collect()
            emit({"event": "progress", "kind": "annotate", "current": count, "total": total, "percent": round(count * 100 / total, 1)})
    except RuntimeError as exc:
        if device != "cpu" and "out of memory" in str(exc).lower():
            try:
                import torch
                torch.cuda.empty_cache()
            except Exception:
                pass
            return annotate({**request, "device": "cpu"})
        raise
    return {"ok": True, "processed": count}


def train(request):
    YOLO = load_ultralytics()
    model = YOLO(request["model"])
    def on_epoch_end(trainer):
        epoch = int(getattr(trainer, "epoch", 0)) + 1
        total = int(getattr(trainer, "epochs", request.get("epochs", 50)))
        emit({"event": "progress", "kind": "train", "current": epoch, "total": total, "percent": round(epoch * 100 / max(1, total), 1)})
    model.add_callback("on_train_epoch_end", on_epoch_end)
    result = model.train(
        data=request["data"],
        epochs=int(request.get("epochs", 50)),
        imgsz=int(request.get("image_size", 640)),
        batch=int(request.get("batch", 8)),
        device=resolve_device(request.get("device", "cpu")),
        project=request.get("project", "runs"),
        name=request.get("name", "dataset"),
    )
    save_dir = str(getattr(result, "save_dir", ""))
    return {"ok": True, "save_dir": save_dir}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True)
    args = parser.parse_args()
    try:
        request = json.loads(Path(args.request).read_text(encoding="utf-8-sig"))
        command = request.get("command")
        if command == "annotate":
            emit(annotate(request))
        elif command == "train":
            emit(train(request))
        else:
            raise ValueError(f"Неизвестная команда: {command}")
    except Exception as exc:
        emit({"ok": False, "error": str(exc)})
        sys.exit(1)


if __name__ == "__main__":
    main()
