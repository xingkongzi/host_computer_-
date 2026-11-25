QT += core network
QT -= gui
CONFIG += console release
CONFIG -= app_bundle debug
TARGET = QtWireFixture
TEMPLATE = app
SOURCES += $$PWD/fixture.cpp
include(../../Adapters/Qt/updatehost.pri)
DESTDIR = $$OUT_PWD
