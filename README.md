# Shader Graph Node Exporter

Package: `com.nachowhy.shadergraph-node-exporter`

Right-click a selected Shader Graph node and choose **Export Node Output...** to render one of that node's previewable output slots to disk.

Supported formats:

- PNG
- EXR
- JPG
- TGA

EXR exports use a floating-point render target and preserve HDR values. PNG, JPG, and TGA use LDR output.

For nodes with multiple previewable outputs, choose the output port in the export window. Exposed graph properties can be overridden before rendering. Time node output can be exported at the current editor time or at a fixed manual time.

Exports can be supersampled with Off, 2x, 4x, or 8x anti-aliasing. The final downsample can use either a Sharp weighted resolve or a Smooth box resolve. When the virtual internal render size reaches 8192 pixels or higher in either dimension, the exporter renders in bounded tiles instead of creating one huge preview render texture.

## Compatibility

This package is built and tested against Unity 6000.3.18f1 with Shader Graph 17.3.0 / URP 17.3.0.

Treat Unity 6000.3.x with Shader Graph 17.3.x as the supported version range. The package depends on Shader Graph editor internals, including preview generation and graph view types, so older Unity versions, earlier Unity 6 streams, and future Shader Graph releases may require code changes even if the package imports successfully.
