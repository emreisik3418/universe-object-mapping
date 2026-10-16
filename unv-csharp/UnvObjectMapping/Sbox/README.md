# Sbox proxy

Bu klasördeki `HsmServiceProxy.cs` dosyası **elle yazılmaz**, WSDL'den üretilir:

```
unv-csharp\tools\generate-sbox-proxy.bat "<Sbox endpoint>?wsdl"
```

Dosya `.gitignore`'dadır (servis sözleşmesi ortama özel). Proje bu dosya olmadan derlenmez.
