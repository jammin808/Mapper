/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "Folds the picture onto itself: the left half over the right, the top half over the bottom, or both.",
    "CREDIT": "PlayMap (MIT)",
    "CATEGORIES": ["Geometry"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "mode", "TYPE": "long", "LABEL": "Fold", "VALUES": [0, 1, 2], "LABELS": ["Left to right", "Top to bottom", "Both"], "DEFAULT": 0 }
    ]
}*/
void main() {
    vec2 uv = isf_FragNormCoord;
    if (mode != 1 && uv.x > 0.5)
        uv.x = 1.0 - uv.x;
    if (mode != 0 && uv.y < 0.5)
        uv.y = 1.0 - uv.y;
    gl_FragColor = IMG_NORM_PIXEL(inputImage, uv);
}
