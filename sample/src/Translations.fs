﻿module Translations

open System
open Fable.Core
open Types

let private translations = JsInterop.importDefault "./translations.json?js"

let fetchTranslations() : TranslationCollection = translations

let matchTranslationLanguage
  (translations: TranslationCollection option, language: Language)
  =

  match translations, language with
  | None, _ -> None
  | Some translations, EnUs -> translations["en-us"]
  | Some translations, DeDe -> translations["de-de"]
  | Some translations, EsMx -> translations["es-mx"]
  | Some translations, Unknown mappingName -> translations[mappingName]

let getTranslationValue
  (translationKey: string)
  (language: TranslationMap option)
  =
  language |> Option.map(fun trMap -> trMap[translationKey]) |> Option.flatten

let T
  (store: IObservable<TranslationCollection option * Language>)
  (key: string, defValue: string)
  =
  Observable.map
    (matchTranslationLanguage
     >> (getTranslationValue key)
     >> Option.defaultValue defValue)
    store
