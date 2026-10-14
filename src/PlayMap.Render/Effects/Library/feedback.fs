/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "Trails: each frame keeps the brighter of the picture and the last frame faded by decay, zoomed about the centre. It integrates from frame to frame, so two nodes agree only from the same start.",
    "CREDIT": "PlayMap (MIT)",
    "CATEGORIES": ["Time"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "decay", "TYPE": "float", "LABEL": "Decay", "DEFAULT": 0.9, "MIN": 0.0, "MAX": 0.999, "IDENTITY": 0.0 },
        { "NAME": "zoom", "TYPE": "float", "LABEL": "Zoom", "DEFAULT": 1.0, "MIN": 0.5, "MAX": 2.0 }
    ],
    "PASSES": [
        { "TARGET": "trail", "PERSISTENT": true, "FLOAT": true },
        { }
    ]
}*/
void main() {
    if (PASSINDEX == 0) {
        vec2 uv = (isf_FragNormCoord - vec2(0.5)) / max(zoom, 0.01) + vec2(0.5);
        vec4 last = IMG_NORM_PIXEL(trail, uv);
        vec4 now = IMG_THIS_PIXEL(inputImage);
        gl_FragColor = max(now, last * decay);
    } else {
        gl_FragColor = IMG_THIS_PIXEL(trail);
    }
}
