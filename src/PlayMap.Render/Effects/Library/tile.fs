/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "The whole picture repeated in a grid of tiles across and down.",
    "CREDIT": "PlayMap",
    "CATEGORIES": ["Geometry"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "across", "TYPE": "float", "LABEL": "Across", "DEFAULT": 2.0, "MIN": 1.0, "MAX": 16.0, "IDENTITY": 1.0 },
        { "NAME": "down", "TYPE": "float", "LABEL": "Down", "DEFAULT": 2.0, "MIN": 1.0, "MAX": 16.0, "IDENTITY": 1.0 }
    ]
}*/
void main() {
    vec2 n = vec2(max(floor(across), 1.0), max(floor(down), 1.0));
    gl_FragColor = IMG_NORM_PIXEL(inputImage, fract(isf_FragNormCoord * n));
}
