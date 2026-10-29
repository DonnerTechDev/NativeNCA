#!/usr/bin/env python3
"""Stylized QR Code Generator for NativeNCA.

Generates beautiful, modern SVG and PNG QR codes with customizable dot shapes,
finder pattern eye designs, brand colors, and center logos (Kaspi, NativeNCA shield, etc.).

Usage:
    python tools/style_qr.py --data "https://kaspi.kz/pay/..." --output site/assets/kaspi-qr.svg
    python tools/style_qr.py --style dots --eye-style rounded --eye-color "#F14635" --logo kaspi
    python tools/style_qr.py --help
"""
import argparse
import sys
from pathlib import Path

try:
    import qrcode
    from qrcode.constants import ERROR_CORRECT_H
except ImportError:
    print("Error: 'qrcode' package is required. Run 'pip install qrcode'.", file=sys.stderr)
    sys.exit(1)


def generate_styled_qr(
    data: str,
    output_path: str = "site/assets/kaspi-qr.svg",
    style: str = "rounded",
    eye_style: str = "rounded",
    color: str = "#18181B",
    eye_color: str = "#F14635",
    bg_color: str = "#FFFFFF",
    logo: str = "kaspi",
    margin: int = 2,
    size: int = 360,
    logo_size_modules: int = 7,
) -> str:
    """Generate a stylized SVG QR code."""
    qr = qrcode.QRCode(
        error_correction=ERROR_CORRECT_H,
        border=0,
    )
    qr.add_data(data)
    qr.make(fit=True)
    matrix = qr.get_matrix()
    n = len(matrix)

    # Calculate dimensions
    total_dim = n + 2 * margin
    scale = size / total_dim

    # Finder patterns locations: (row, col)
    # Top-Left: (0, 0), Top-Right: (0, n-7), Bottom-Left: (n-7, 0)
    finders = [
        (0, 0),
        (0, n - 7),
        (n - 7, 0),
    ]

    # Logo region in matrix coordinates
    center = n // 2
    logo_half = logo_size_modules // 2
    has_logo = logo.lower() != "none"
    logo_min = center - logo_half
    logo_max = center + logo_half

    def is_finder_zone(r: int, c: int) -> bool:
        if r < 8 and c < 8:
            return True
        if r < 8 and c >= n - 8:
            return True
        if r >= n - 8 and c < 8:
            return True
        return False

    def is_logo_zone(r: int, c: int) -> bool:
        if not has_logo:
            return False
        return logo_min <= r <= logo_max and logo_min <= c <= logo_max

    svg_parts = []
    svg_parts.append(
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" '
        f'width="{size}" height="{size}" role="img" aria-label="QR Code">'
    )

    # Background
    if bg_color and bg_color.lower() != "transparent":
        svg_parts.append(f'  <rect width="{size}" height="{size}" rx="16" fill="{bg_color}" />')

    # Data modules group
    svg_parts.append(f'  <g id="data-modules" fill="{color}">')

    for r in range(n):
        for c in range(n):
            if not matrix[r][c]:
                continue
            if is_finder_zone(r, c) or is_logo_zone(r, c):
                continue

            x = (c + margin) * scale
            y = (r + margin) * scale

            if style == "dots":
                cx = x + scale / 2
                cy = y + scale / 2
                radius = scale * 0.44
                svg_parts.append(f'    <circle cx="{cx:.2f}" cy="{cy:.2f}" r="{radius:.2f}" />')
            elif style == "squircle":
                pad = scale * 0.04
                w = scale - 2 * pad
                rx = scale * 0.38
                svg_parts.append(
                    f'    <rect x="{x + pad:.2f}" y="{y + pad:.2f}" width="{w:.2f}" height="{w:.2f}" rx="{rx:.2f}" />'
                )
            elif style == "square":
                svg_parts.append(
                    f'    <rect x="{x:.2f}" y="{y:.2f}" width="{scale:.2f}" height="{scale:.2f}" />'
                )
            else:  # rounded default
                pad = scale * 0.06
                w = scale - 2 * pad
                rx = scale * 0.28
                svg_parts.append(
                    f'    <rect x="{x + pad:.2f}" y="{y + pad:.2f}" width="{w:.2f}" height="{w:.2f}" rx="{rx:.2f}" />'
                )

    svg_parts.append('  </g>')

    # Finder patterns / eyes
    svg_parts.append(f'  <g id="finder-eyes">')
    for fr, fc in finders:
        fx = (fc + margin) * scale
        fy = (fr + margin) * scale
        fw = 7 * scale

        if eye_style == "circle":
            # Outer ring
            ocx = fx + fw / 2
            ocy = fy + fw / 2
            r_out = fw / 2 - scale * 0.5
            sw = scale
            svg_parts.append(
                f'    <circle cx="{ocx:.2f}" cy="{ocy:.2f}" r="{r_out:.2f}" fill="none" stroke="{eye_color}" stroke-width="{sw:.2f}" />'
            )
            # Inner circle
            r_in = 1.5 * scale
            svg_parts.append(
                f'    <circle cx="{ocx:.2f}" cy="{ocy:.2f}" r="{r_in:.2f}" fill="{eye_color}" />'
            )
        elif eye_style == "square":
            # Outer frame
            sw = scale
            svg_parts.append(
                f'    <rect x="{fx + sw/2:.2f}" y="{fy + sw/2:.2f}" width="{fw - sw:.2f}" height="{fw - sw:.2f}" fill="none" stroke="{eye_color}" stroke-width="{sw:.2f}" />'
            )
            # Inner square
            ix = fx + 2 * scale
            iy = fy + 2 * scale
            iw = 3 * scale
            svg_parts.append(
                f'    <rect x="{ix:.2f}" y="{iy:.2f}" width="{iw:.2f}" height="{iw:.2f}" fill="{eye_color}" />'
            )
        elif eye_style == "leaf":
            # Stylized asymmetrical rounded corners (leaf look)
            sw = scale
            rx = 2.4 * scale
            svg_parts.append(
                f'    <rect x="{fx + sw/2:.2f}" y="{fy + sw/2:.2f}" width="{fw - sw:.2f}" height="{fw - sw:.2f}" rx="{rx:.2f}" fill="none" stroke="{eye_color}" stroke-width="{sw:.2f}" />'
            )
            ix = fx + 2 * scale
            iy = fy + 2 * scale
            iw = 3 * scale
            svg_parts.append(
                f'    <circle cx="{ix + iw/2:.2f}" cy="{iy + iw/2:.2f}" r="{iw * 0.45:.2f}" fill="{eye_color}" />'
            )
        else:  # rounded default
            sw = scale
            rx = 2.0 * scale
            svg_parts.append(
                f'    <rect x="{fx + sw/2:.2f}" y="{fy + sw/2:.2f}" width="{fw - sw:.2f}" height="{fw - sw:.2f}" rx="{rx:.2f}" fill="none" stroke="{eye_color}" stroke-width="{sw:.2f}" />'
            )
            ix = fx + 2 * scale
            iy = fy + 2 * scale
            iw = 3 * scale
            irx = 1.2 * scale
            svg_parts.append(
                f'    <rect x="{ix:.2f}" y="{iy:.2f}" width="{iw:.2f}" height="{iw:.2f}" rx="{irx:.2f}" fill="{eye_color}" />'
            )

    svg_parts.append('  </g>')

    # Center Logo
    if has_logo:
        lx = (logo_min + margin) * scale
        ly = (logo_min + margin) * scale
        lw = (logo_max - logo_min + 1) * scale

        svg_parts.append(f'  <g id="center-logo">')
        # Background backing plate with rounded corners
        bg_fill = bg_color if bg_color and bg_color.lower() != "transparent" else "#FFFFFF"
        plate_pad = scale * 0.2
        svg_parts.append(
            f'    <rect x="{lx - plate_pad:.2f}" y="{ly - plate_pad:.2f}" '
            f'width="{lw + 2 * plate_pad:.2f}" height="{lw + 2 * plate_pad:.2f}" '
            f'rx="{scale * 1.5:.2f}" fill="{bg_fill}" />'
        )

        badge_pad = scale * 0.5
        bx = lx + badge_pad
        by = ly + badge_pad
        bw = lw - 2 * badge_pad
        brx = bw * 0.28

        if logo == "kaspi":
            # Kaspi Red badge with signature 'K'
            svg_parts.append(
                f'    <rect x="{bx:.2f}" y="{by:.2f}" width="{bw:.2f}" height="{bw:.2f}" rx="{brx:.2f}" fill="#F14635" />'
            )
            # Stylized white Kaspi "K"
            k_font_size = bw * 0.72
            svg_parts.append(
                f'    <text x="{bx + bw/2:.2f}" y="{by + bw/2 + k_font_size * 0.35:.2f}" '
                f'font-family="-apple-system, BlinkMacSystemFont, \'Segoe UI\', Roboto, sans-serif" '
                f'font-size="{k_font_size:.2f}" font-weight="900" fill="#FFFFFF" text-anchor="middle">K</text>'
            )
        elif logo == "shield":
            # NativeNCA Shield icon badge
            svg_parts.append(
                f'    <rect x="{bx:.2f}" y="{by:.2f}" width="{bw:.2f}" height="{bw:.2f}" rx="{brx:.2f}" fill="#005FB8" />'
            )
            # Shield path inside
            svg_parts.append(
                f'    <g transform="translate({bx + bw*0.2:.2f}, {by + bw*0.2:.2f}) scale({bw*0.6 / 24:.3f})" fill="#FFFFFF">'
                f'      <path d="M14 11a2 2 0 0 1-1.25 1.85v2.4a.75.75 0 0 1-1.5 0v-2.4A2 2 0 1 1 14 11M3.75 5a.75.75 0 0 0-.75.75V11c0 5 2.96 8.68 8.73 10.95q.27.1.54 0C18.04 19.68 21 16 21 11V5.75a.75.75 0 0 0-.75-.75q-3.99 0-7.8-2.85a.75.75 0 0 0-.9 0Q7.74 5 3.75 5"/>'
                f'    </g>'
            )
        elif logo == "heart":
            # Heart icon badge
            svg_parts.append(
                f'    <rect x="{bx:.2f}" y="{by:.2f}" width="{bw:.2f}" height="{bw:.2f}" rx="{brx:.2f}" fill="#F14635" />'
            )
            svg_parts.append(
                f'    <g transform="translate({bx + bw*0.2:.2f}, {by + bw*0.2:.2f}) scale({bw*0.6 / 24:.3f})" fill="#FFFFFF">'
                f'      <path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"/>'
                f'    </g>'
            )

        svg_parts.append('  </g>')

    svg_parts.append('</svg>\n')
    svg_content = "\n".join(svg_parts)

    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(svg_content, encoding="utf-8")
    return svg_content


def main():
    parser = argparse.ArgumentParser(description="Generate stylized QR codes for NativeNCA.")
    parser.add_argument("--data", "-d", default="https://kaspi.kz/pay/nativenca", help="QR payload or URL")
    parser.add_argument("--output", "-o", default="site/assets/kaspi-qr.svg", help="Output SVG path")
    parser.add_argument("--style", choices=["rounded", "dots", "squircle", "square"], default="rounded", help="Data module dot style")
    parser.add_argument("--eye-style", choices=["rounded", "circle", "square", "leaf"], default="rounded", help="Finder pattern eye style")
    parser.add_argument("--color", default="#18181B", help="Foreground color for dots")
    parser.add_argument("--eye-color", default="#F14635", help="Color for finder pattern eyes")
    parser.add_argument("--bg", default="#FFFFFF", help="Background color")
    parser.add_argument("--logo", choices=["kaspi", "shield", "heart", "none"], default="kaspi", help="Center logo badge")
    parser.add_argument("--size", type=int, default=360, help="Output image size in pixels")
    parser.add_argument("--margin", type=int, default=2, help="Quiet zone margin in modules")

    args = parser.parse_args()
    generate_styled_qr(
        data=args.data,
        output_path=args.output,
        style=args.style,
        eye_style=args.eye_style,
        color=args.color,
        eye_color=args.eye_color,
        bg_color=args.bg,
        logo=args.logo,
        margin=args.margin,
        size=args.size,
    )
    print(f"Generated stylized QR code: {args.output}")


if __name__ == "__main__":
    main()
