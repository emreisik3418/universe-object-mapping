-- Description update sorgusu (örnek). Kendi tablonuza göre düzenleyin.
-- ZORUNLU: iki kolon, bu alias'larla:
--   OBJECT_PATH : universe'teki obje yolu, klasörler '\' veya '/' ile:  'Müşteri\Müşteri No'
--                 (attribute ise dimension adı da yolda: 'Müşteri\Müşteri No\Segment')
--   DESCRIPTION : yazılacak açıklama (boşsa o obje atlanır, mevcut açıklama silinmez)
SELECT t.KLASOR || '\' || t.OBJE_ADI AS OBJECT_PATH,
       t.ACIKLAMA                    AS DESCRIPTION
  FROM SEMA.ACIKLAMA_TABLOSU t
 WHERE t.UNIVERSE_ADI = 'Satis'
